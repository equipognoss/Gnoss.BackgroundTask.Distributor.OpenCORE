using System;
using System.Collections.Generic;
using System.Threading;
using System.Data;
using Es.Riam.Gnoss.Servicios;
using Es.Riam.Gnoss.Logica.Live;
using Es.Riam.Gnoss.Recursos;
using Es.Riam.Gnoss.AD.Live.Model;
using Es.Riam.Gnoss.AD.Live;
using Es.Riam.Gnoss.Logica.ParametroAplicacion;
using Es.Riam.Gnoss.Logica.Facetado;
using Es.Riam.Gnoss.Logica.Documentacion;
using Es.Riam.Gnoss.Logica.Comentario;
using Es.Riam.Gnoss.Logica.Identidad;
using Es.Riam.Gnoss.Web.Controles.Documentacion;
using Es.Riam.Gnoss.AD.ServiciosGenerales;
using Es.Riam.Gnoss.AD.ParametroAplicacion;
using Es.Riam.Gnoss.Logica.ServiciosGenerales;
using Es.Riam.Gnoss.AD.Facetado;
using Es.Riam.Gnoss.Web.MVC.Models;
using Es.Riam.Gnoss.Web.Controles.ParametroAplicacionGBD;
using Es.Riam.Gnoss.Elementos.ParametroAplicacion;
using Es.Riam.Gnoss.AD.EntityModel;
using System.Linq;
using Es.Riam.Gnoss.AD.EncapsuladoDatos;
using Es.Riam.Gnoss.RabbitMQ;
using Es.Riam.Util;
using Es.Riam.Gnoss.Util.General;
using Newtonsoft.Json;
using Microsoft.Extensions.DependencyInjection;
using Es.Riam.Gnoss.Util.Configuracion;
using Es.Riam.Gnoss.AD.EntityModelBASE;
using Es.Riam.Gnoss.AD.Virtuoso;
using Es.Riam.Gnoss.CL;
using Es.Riam.Gnoss.AD.Facetado.Model;
using Es.Riam.Gnoss.AD.BASE_BD;
using Es.Riam.AbstractsOpen;

namespace Es.Riam.Gnoss.ServicioRepartoColas
{
    public class Controller : ControladorServicioGnoss
    {
        #region Constantes

        private const string COLA = "cola";
        private const string EXCHANGE = "";

        #endregion

        #region Miembros

        /// <summary>
        /// Data set con los recursos a procesar
        /// </summary>
        private LiveDS mLiveDSRecursos = new LiveDS();

        /// <summary>
        /// Data set con los miembros a procesar
        /// </summary>
        private LiveDS mLiveDSMiembros = new LiveDS();

        /// <summary>
        /// Lista con los nombres de las ontologías de las que hay que descartar las filas de sus recursos.
        /// </summary>
        private List<string> mOntologiasDescartarFilas;

        private int? mTablaBaseProyectoID;

        #endregion

        #region Constructores

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="pFicheroConfiguracionBD">Ruta al archivo de configuración de la base de datos</param>
        public Controller(IServiceScopeFactory scopedFactory, ConfigService configService)
            : base(scopedFactory, configService)
        {
        }

        protected override ControladorServicioGnoss ClonarControlador()
        {
            return new Controller(ScopedFactory, mConfigService);
        }

        #endregion

        #region Métodos generales
        
        public bool ProcesarItem(string pFila)
        {
            using (var scope = ScopedFactory.CreateScope())
            {
                EntityContext entityContext = scope.ServiceProvider.GetRequiredService<EntityContext>();
                EntityContextBASE entityContextBASE = scope.ServiceProvider.GetRequiredService<EntityContextBASE>();
                UtilidadesVirtuoso utilidadesVirtuoso = scope.ServiceProvider.GetRequiredService<UtilidadesVirtuoso>();
                LoggingService loggingService = scope.ServiceProvider.GetRequiredService<LoggingService>();
                VirtuosoAD virtuosoAD = scope.ServiceProvider.GetRequiredService<VirtuosoAD>();
                RedisCacheWrapper redisCacheWrapper = scope.ServiceProvider.GetRequiredService<RedisCacheWrapper>();
                GnossCache gnossCache = scope.ServiceProvider.GetRequiredService<GnossCache>();
                IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication = scope.ServiceProvider.GetRequiredService<IServicesUtilVirtuosoAndReplication>();
                try
                {
                    ComprobarCancelacionHilo();

                    System.Diagnostics.Debug.WriteLine($"ProcesarItem, {pFila}!");

                    if (!string.IsNullOrEmpty(pFila))
                    {
                        object[] itemArray = JsonConvert.DeserializeObject<object[]>(pFila);
                        LiveDS.ColaRow filaCola = (LiveDS.ColaRow)new LiveDS().Cola.Rows.Add(itemArray);
                        itemArray = null;

                        ProcesarFilasDeCola(filaCola, entityContext, loggingService, virtuosoAD, redisCacheWrapper, entityContextBASE, gnossCache, servicesUtilVirtuosoAndReplication);

                        filaCola = null;
                        servicesUtilVirtuosoAndReplication.ConexionAfinidad = "";

                        ControladorConexiones.CerrarConexiones(false);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    loggingService.GuardarLogError(ex);
                    return true;
                }
            }
        }

        /// <summary>
        /// Procesa cada suscripcion para crear su notificación a través de RabbitMQ
        /// </summary>
        private void RealizarMantenimientoRabbitMQ(LoggingService loggingService, bool reintentar = true)
        {
            if (mConfigService.ExistRabbitConnection(RabbitMQClient.BD_SERVICIOS_WIN))
            {
                RabbitMQClient.ReceivedDelegate funcionProcesarItem = new RabbitMQClient.ReceivedDelegate(ProcesarItem);
                RabbitMQClient.ShutDownDelegate funcionShutDown = new RabbitMQClient.ShutDownDelegate(OnShutDown);

                RabbitMQClient rabbitMQClient = new RabbitMQClient(RabbitMQClient.BD_SERVICIOS_WIN, COLA, loggingService, mConfigService, EXCHANGE, COLA);

                try
                {
                    rabbitMQClient.ObtenerElementosDeCola(funcionProcesarItem, funcionShutDown);
                    mReiniciarLecturaRabbit = false;
                }
                catch (Exception ex)
                {
                    mReiniciarLecturaRabbit = true;
                    loggingService.GuardarLogError(ex);
                }
            }
        }

        private void ProcesarFilasDeCola(LiveDS.ColaRow pColaRow, EntityContext entityContext, LoggingService loggingService, VirtuosoAD virtuosoAD, RedisCacheWrapper redisCacheWrapper, EntityContextBASE entityContextBASE, GnossCache gnossCache, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            if (pColaRow.Tipo.Equals((int)TipoLive.Miembro))
            {
                if (pColaRow.NumIntentos < 6)
                {
                    if (pColaRow.Accion == (int)AccionLive.PerfilEditado ||
                        pColaRow.Accion == (int)AccionLive.EstadoCambiado ||
                        pColaRow.Accion == (int)AccionLive.Agregado ||
                        pColaRow.Accion == (int)AccionLive.Eliminado ||
                        pColaRow.Accion == (int)AccionLive.RecursoAgregado ||
                        pColaRow.Accion == (int)AccionLive.ComprobacionCaducidad ||
                        pColaRow.Accion == (int)AccionLive.ComunidadAbierta ||
                        pColaRow.Accion == (int)AccionLive.ComunidadCerrada ||
                        pColaRow.Accion == (int)AccionLive.ReprocesarEventoHomeProyecto)
                    {
                        InsertarFilaLiveEnColaUsuario("ColaUsuariosEspecifico", pColaRow, loggingService);
                    }

                    if (pColaRow.Accion == (int)AccionLive.PerfilEditado)
                    {
                        RecalcularHTMLUsuario(pColaRow, entityContext, loggingService, servicesUtilVirtuosoAndReplication);
                    }
                    pColaRow.Delete();
                }

                ControladorConexiones.CerrarConexiones();
            }
            else
            {
                if (pColaRow.NumIntentos < 6)
                {
                    if (pColaRow.Accion == (int)AccionLive.ComentarioAgregado ||
                        pColaRow.Accion == (int)AccionLive.ComentarioEditado ||
                        pColaRow.Accion == (int)AccionLive.ComentarioEliminado)
                    {
                        ProcesarFilaComentarioPerfiles(pColaRow, entityContext, loggingService, virtuosoAD, redisCacheWrapper, gnossCache, entityContextBASE, servicesUtilVirtuosoAndReplication);
                    }

                    if (HayQueProcesarFila(pColaRow, entityContext, loggingService, servicesUtilVirtuosoAndReplication))
                    {
                        if (pColaRow.Accion == (int)AccionLive.Agregado ||
                            pColaRow.Accion == (int)AccionLive.Eliminado ||
                            (pColaRow.Accion == (int)AccionLive.Editado && pColaRow.InfoExtra.Contains(Constantes.PRIVACIDAD_CAMBIADA)) ||
                            pColaRow.Accion == (int)AccionLive.Votado ||
                            pColaRow.Accion == (int)AccionLive.ComentarioAgregado ||
                            pColaRow.Accion == (int)AccionLive.ReprocesarEventoHomeProyecto)
                        {
                            InsertarFilaLiveEnColaUsuario("ColaUsuarios", pColaRow, loggingService);
                            InsertarFilaLiveEnColaUsuario("ColaUsuariosEspecifico", pColaRow, loggingService);
                        }
                    }

                    pColaRow.Delete();
                }

                ControladorConexiones.CerrarConexiones();
            }
        }

        private void InsertarFilaLiveEnColaUsuario(string pColaRabbit, LiveDS.ColaRow pElemento, LoggingService loggingService)
        {
            string rabbitBD = RabbitMQClient.BD_SERVICIOS_WIN;
            string exchange = "";
            if (mConfigService.ExistRabbitConnection(rabbitBD))
            {
                try
                {
                    LiveUsuariosDS.ColaUsuariosRow colaUsuarioRow = new LiveUsuariosDS().ColaUsuarios.NewColaUsuariosRow();
                    colaUsuarioRow.ColaId = pElemento.ColaId;
                    colaUsuarioRow.Accion = pElemento.Accion;
                    colaUsuarioRow.Fecha = pElemento.Fecha;
                    colaUsuarioRow.Id = pElemento.Id;
                    colaUsuarioRow.InfoExtra = pElemento.InfoExtra;
                    colaUsuarioRow.NumIntentos = pElemento.NumIntentos;
                    colaUsuarioRow.Prioridad = pElemento.Prioridad;
                    colaUsuarioRow.ProyectoId = pElemento.ProyectoId;
                    colaUsuarioRow.Tipo = pElemento.Tipo;

                    using (RabbitMQClient rMQ = new RabbitMQClient(rabbitBD, pColaRabbit, loggingService, mConfigService, exchange, pColaRabbit))
                    {
                        rMQ.AgregarElementoACola(JsonConvert.SerializeObject(colaUsuarioRow.ItemArray));
                    }
                }
                catch (Exception ex)
                {
                    loggingService.GuardarLogError(ex);
                    throw;
                }
            }
        }

        

        /// <summary>
        /// Procesa cada suscripcion para crear su notificacion correspondiente
        /// </summary>
        public override void RealizarMantenimiento(EntityContext entityContext, EntityContextBASE entityContextBASE, UtilidadesVirtuoso utilidadesVirtuoso, LoggingService loggingService, RedisCacheWrapper redisCacheWrapper, GnossCache gnossCache, VirtuosoAD virtuosoAD, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            #region Establezco el dominio de la cache

            ParametroAplicacionCN parametroApliCN = new ParametroAplicacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            //ParametroAplicacionDS paramApliDS = parametroApliCN.ObtenerConfiguracionGnoss();
            //parametroApliCN.InicializarEntityContext();
            GestorParametroAplicacion gestorParametroAplicacion = new GestorParametroAplicacion();
            ParametroAplicacionGBD parametroAplicacionGBD = new ParametroAplicacionGBD(loggingService, entityContext, mConfigService);
            parametroAplicacionGBD.ObtenerConfiguracionGnoss(gestorParametroAplicacion);
            //parametroApliCN.Dispose();

            // mDominio = gestorParametroAplicacion.ParametroAplicacion.Find("Parametro='UrlIntragnoss'").Valor;
            mDominio = gestorParametroAplicacion.ParametroAplicacion.Find(parametroApp => parametroApp.Parametro.Equals("UrlIntragnoss")).Valor;
            mDominio = mDominio.Replace("http://", "").Replace("www.", "");

            if (mDominio[mDominio.Length - 1] == '/')
            {
                mDominio = mDominio.Substring(0, mDominio.Length - 1);
            }

            //mUrlIntragnoss = gestorParametroAplicacion.ParametroAplicacion.Select("Parametro='UrlIntragnoss'").Valor;
            mUrlIntragnoss = gestorParametroAplicacion.ParametroAplicacion.Find(parametroApp => parametroApp.Parametro.Equals("UrlIntragnoss")).Valor;
            CargarOntosDescartarFilas(gestorParametroAplicacion);

            //paramApliDS.Dispose();

            #endregion

            RealizarMantenimientoRabbitMQ(loggingService);
        }

        /// <summary>
        /// Carga las ontologías de las que se deben descartar filas.
        /// </summary>
        private void CargarOntosDescartarFilas(GestorParametroAplicacion pParamApliDS)
        {
            //List<ParametroAplicacion> filasParam = pParamApliDS.ParametroAplicacion.Select("Parametro='" + TiposParametrosAplicacion.OntologiasNoLive + "'");
            List<ParametroAplicacion> filasParam = pParamApliDS.ParametroAplicacion.Where(parametroApp => parametroApp.Parametro.Equals(TiposParametrosAplicacion.OntologiasNoLive)).ToList();
            if (filasParam.Count > 0)
            {
                mOntologiasDescartarFilas = new List<string>();

                foreach (string onto in filasParam[0].Valor.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    mOntologiasDescartarFilas.Add(onto);
                }
            }
        }

        /// <summary>
        /// Indica si hay que procesar la fila.
        /// </summary>
        /// <param name="pColaRow">Fila de la cola</param>
        /// <returns>TRUE si hay que procesar la fila, FALSE si no</returns>
        private bool HayQueProcesarFila(LiveDS.ColaRow pColaRow, EntityContext entityContext, LoggingService loggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            try
            {
                if (mOntologiasDescartarFilas != null)
                {
                    Guid documentoID = ObtenerIDElementoPrincipal(pColaRow, entityContext, loggingService, servicesUtilVirtuosoAndReplication);

                    DocumentacionCN docCN = new DocumentacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                    string ontologia = docCN.ObtenerEnlaceDocumentoVinculadoADocumento(documentoID);
                    docCN.Dispose();

                    if (!string.IsNullOrEmpty(ontologia) && mOntologiasDescartarFilas.Contains(ontologia))
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                loggingService.GuardarLog($"Error al mirar si hay que procesar una fila o no: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Obtiene el elemento principal de una fila del LIVE.
        /// </summary>
        /// <param name="pFilaCola">Fila cola</param>
        /// <returns>Elemento principal de una fila del LIVE</returns>
        private Guid ObtenerIDElementoPrincipal(LiveDS.ColaRow pFilaCola, EntityContext entityContext, LoggingService loggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            TipoLive tipo = (TipoLive)pFilaCola.Tipo;
            AccionLive accion = (AccionLive)pFilaCola.Accion;

            Guid elementoID = pFilaCola.Id;
            DocumentacionCN docCN = null;

            switch (tipo)
            {
                case TipoLive.Recurso:
                case TipoLive.Pregunta:
                case TipoLive.Debate:
                    switch (accion)
                    {
                        case AccionLive.ComentarioAgregado:
                            docCN = new DocumentacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                            elementoID = docCN.ObtenerIDDocumentoDeComentarioPorID(pFilaCola.Id);
                            docCN.Dispose();
                            break;
                        case AccionLive.Votado:
                            docCN = new DocumentacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                            elementoID = docCN.ObtenerIDDocumentoDeVotoPorID(pFilaCola.Id);
                            docCN.Dispose();
                            break;
                    }

                    break;
            }

            return elementoID;
        }


        /// <summary>
        /// Manda al base las filas de comentario.
        /// </summary>
        /// <param name="pColaRow">Fila de cola</param>personal o de ORG</param>
        private void ProcesarFilaComentarioPerfiles(LiveDS.ColaRow pColaRow, EntityContext entityContext, LoggingService loggingService, VirtuosoAD virtuosoAD, RedisCacheWrapper redisCacheWrapper, GnossCache gnossCache, EntityContextBASE entityContextBASE, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            ComentarioCN comentCN = new ComentarioCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            DocumentacionCN docCN = new DocumentacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            IdentidadCN idenCN = new IdentidadCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            LiveCN liveCN = new LiveCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);

            List<Guid> listaComentario = new List<Guid>();
            listaComentario.Add(pColaRow.Id);
            AD.EntityModel.Models.Comentario.Comentario comentarioRow = comentCN.ObtenerComentariosDeDocumentosPorComentariosID(listaComentario).ListaComentario.FirstOrDefault();
            Guid recursoID = docCN.ObtenerIDDocumentoDeComentarioPorID(comentarioRow.ComentarioID);

            //Agrego los perfiles de todos los autores de los comentarios del recurso:
            List<Guid> listaPerfilesID = docCN.ObtenerPerfilesAutoresComenariosDocumento(recursoID, pColaRow.ProyectoId, pColaRow.Accion == (short)AccionLive.ComentarioEliminado);

            DataWrapperDocumentacion docDW = new DataWrapperDocumentacion();
            docCN.ObtenerDocumentoPorIDCargarTotal(recursoID, docDW, true, true, null);
            AD.EntityModel.Models.Documentacion.Documento documentoRow = docDW.ListaDocumento.FirstOrDefault();

            foreach (AD.EntityModel.Models.Documentacion.DocumentoWebVinBaseRecursos filaDocWebVin in docDW.ListaDocumentoWebVinBaseRecursos)
            {
                DataWrapperIdentidad idenPublicadorRecDW = idenCN.ObtenerIdentidadPorID(filaDocWebVin.IdentidadPublicacionID.Value, false);
                AD.EntityModel.Models.IdentidadDS.Perfil perfilPublicadorRecRow = idenPublicadorRecDW.ListaPerfil.FirstOrDefault();

                if (!listaPerfilesID.Contains(perfilPublicadorRecRow.PerfilID))
                {
                    listaPerfilesID.Add(perfilPublicadorRecRow.PerfilID);
                }
            }

            DataWrapperIdentidad dataWrapperIdentidad = idenCN.ObtenerIdentidadPorID(comentarioRow.IdentidadID, false);
            AD.EntityModel.Models.IdentidadDS.Perfil perfilComentarioRow = dataWrapperIdentidad.ListaPerfil.FirstOrDefault();

            string ListaPerfiles = "";

            foreach (Guid perfilID in listaPerfilesID)
            {
                if (pColaRow.Accion.Equals((short)AccionLive.ComentarioEliminado))
                {
                    FacetadoCN facetadoCN = new FacetadoCN(mUrlIntragnoss, null, entityContext, loggingService, mConfigService, virtuosoAD, servicesUtilVirtuosoAndReplication);

                    ActualizacionFacetadoCN actualizacionFacetadoCN = new ActualizacionFacetadoCN(mUrlIntragnoss, entityContext, loggingService, mConfigService, virtuosoAD, servicesUtilVirtuosoAndReplication);
                    string usuario = actualizacionFacetadoCN.obtenerIdusuarioDesdePerfil(perfilID).ToString();

                    //comprobar si no leido (virtuoso) y decrementar contador (acido)
                    string consulta = "SELECT ?o from <http://gnoss.com/" + usuario.ToLower() + "> WHERE {<http://gnoss/" + comentarioRow.ComentarioID.ToString().ToUpper() + "> <http://gnoss/Leido> ?o. }";
                    FacetadoDS ds = facetadoCN.LeerDeVirtuoso(consulta, "EstadoComentario", usuario.ToLower());

                    if (ds != null && ds.Tables != null && ds.Tables.Contains("EstadoComentario") && ds.Tables["EstadoComentario"].Rows.Count > 0)
                    {
                        //ds.Tables["EstadoComentario"]
                        DataRow myrow = ds.Tables["EstadoComentario"].Rows[0];
                        string objeto = (string)myrow[0];
                        if (!string.IsNullOrEmpty(objeto) && objeto.Equals("Pendientes de leer"))
                        {
                            liveCN.DisminuirContadorComentariosLeidos(perfilID);
                            liveCN.ActualizarNuevosComentarios(perfilID, true, DateTime.Now);
                        }
                    }

                    facetadoCN.BorrarRecurso(usuario.ToLower(), comentarioRow.ComentarioID);

                    facetadoCN.Dispose();
                }
                else
                {
                    if (perfilComentarioRow.PerfilID != perfilID)
                    {
                        ListaPerfiles += perfilID + "|";
                    }
                }
            }
            if (!string.IsNullOrEmpty(ListaPerfiles))
            {
                ListaPerfiles.Substring(0, ListaPerfiles.Length);
                ControladorDocumentacion controladorDocumentacion = new ControladorDocumentacion(loggingService, entityContext, mConfigService, redisCacheWrapper, gnossCache, entityContextBASE, virtuosoAD, null, servicesUtilVirtuosoAndReplication);
                controladorDocumentacion.AgregarComentarioFacModeloBaseSimple(comentarioRow.ComentarioID, pColaRow.ProyectoId, mFicheroConfiguracionBDBase, mFicheroConfiguracionBD, ListaPerfiles, (AD.BASE_BD.PrioridadBase)pColaRow.Prioridad, TablaBaseProyectoID(entityContext, loggingService, servicesUtilVirtuosoAndReplication));
            }

            liveCN.Dispose();
        }

        private void RecalcularHTMLUsuario(LiveDS.ColaRow pColaRow, EntityContext entityContext, LoggingService loggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            Guid perfilID = pColaRow.Id;

            DocumentacionCN docCN = new DocumentacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            List<AD.EntityModel.Models.Documentacion.Documento> listaRecUsuProy = docCN.ObtenerListaRecursosUsuarioActualizarPorComunidad(perfilID);
            docCN.Dispose();
            //crear lista de filas LiveUsuariosHTML con los 100 recursos más recientes de cada comunidad de este usuario
            List<LiveUsuariosDS.ColaUsuariosRow> listaFilas = new List<LiveUsuariosDS.ColaUsuariosRow>();
            List<Guid> listaDocumentosProcesados = new List<Guid>();

            foreach (AD.EntityModel.Models.Documentacion.Documento filaRecurso in listaRecUsuProy)
            {
                //ProyectoId, Id, Accion, Tipo, NumIntentos, Fecha,Prioridad, InfoExtra
                LiveUsuariosDS liveUsuariosDS = new LiveUsuariosDS();
                LiveUsuariosDS.ColaUsuariosRow fila = liveUsuariosDS.ColaUsuarios.NewColaUsuariosRow();
                fila.ColaId = pColaRow.ColaId;

                Guid proyectoID = Guid.Empty;
                Guid documentoID = filaRecurso.DocumentoID;
                if (filaRecurso.ProyectoID.HasValue)
                {
                    proyectoID = filaRecurso.ProyectoID.Value;
                }
                if (!listaDocumentosProcesados.Contains(documentoID))
                {
                    fila.ProyectoId = proyectoID;
                    fila.Id = documentoID;
                    fila.Accion = (int)AccionLive.PerfilEditado;

                    switch (filaRecurso.Tipo)
                    {
                        case (short)TiposDocumentacion.Pregunta:
                            fila.Tipo = (short)TipoLive.Pregunta;
                            break;
                        case (short)TiposDocumentacion.Debate:
                            fila.Tipo = (short)TipoLive.Debate;
                            break;
                        default:
                            fila.Tipo = (short)TipoLive.Recurso;
                            break;
                    }

                    fila.NumIntentos = pColaRow.NumIntentos;
                    fila.Fecha = pColaRow.Fecha;
                    fila.Prioridad = pColaRow.Prioridad;
                    fila.InfoExtra = pColaRow.InfoExtra;
                    listaFilas.Add(fila);
                    listaDocumentosProcesados.Add(documentoID);
                }
            }
        }

        #endregion

        #region Propiedades

        private int TablaBaseProyectoID(EntityContext entityContext, LoggingService loggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {

            if (!mTablaBaseProyectoID.HasValue)
            {
                ProyectoCN proyectoCN = new ProyectoCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                mTablaBaseProyectoID = proyectoCN.ObtenerTablaBaseProyectoIDProyectoPorID(ProyectoAD.MetaProyecto);
            }

            return mTablaBaseProyectoID.Value;
            
        }

        #endregion

    }
}
