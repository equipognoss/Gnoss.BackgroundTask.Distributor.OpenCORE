![](https://content.gnoss.ws/imagenes/proyectos/personalizacion/7e72bf14-28b9-4beb-82f8-e32a3b49d9d3/cms/logognossazulprincipal.png)

# Gnoss.BackgroundTask.Distributor.OpenCORE

![](https://github.com/equipognoss/Gnoss.BackgroundTask.Distributor.OpenCORE/workflows/BuildDistributor/badge.svg)

Aplicación de segundo plano que recibe un evento de creación o edición de un recurso y notifica al resto de servicios que tienen que realizar alguna acción. Por ejemplo, Community Wall, User Wall, etc.

Este servicio está escuchando la cola de nombre "cola". Se envía un mensaje a esta cola cada vez que se crea, comparte, edita o elimina un recurso; se crea, edita o elimina un comentario; se da de alta, edita su perfil o se da de baja una persona de una comunidad desde la Web o el API, para que este servicio distribuya ese evento a otros servicios a través de estas colas: 

* ColaUsuarios: es procesada por el servicio Gnoss.BackgroundTask.CommunityWall
* ColaUsuariosEspecifico: es procesada por el servicio Gnoss.BackgroundTask.CommunityWall

Si el evento es de un comentario a un recurso (un comentario nuevo, editado o eliminado), también genera en el grafo del usuario que ha creado ese recurso el comentario, para que pueda verlo desde su bandeja de comentarios. 

Configuración estandar de esta aplicación en el archivo docker-compose.yml: 

```yml
distributor:
    image: gnoss/gnoss.backgroundtask.distributor.opencore
    env_file: .env
    environment:
     virtuosoConnectionString: ${virtuosoConnectionString}
     acid: ${acid}
     base: ${base}
     RabbitMQ__colaServiciosWin: ${RabbitMQ}
     RabbitMQ__colaReplicacion: ${RabbitMQ}
     redis__redis__ip__master: ${redis__redis__ip__master}
     redis__redis__bd: ${redis__redis__bd}
     redis__redis__timeout: ${redis__redis__timeout}
     redis__recursos__ip__master: ${redis__recursos__ip__master}
     redis__recursos__bd: ${redis__recursos_bd}
     redis__recursos__timeout: ${redis__recursos_timeout}
     redis__liveUsuarios__ip__master: ${redis__liveUsuarios__ip__master}
     redis__liveUsuarios__bd: ${redis__liveUsuarios_bd}
     redis__liveUsuarios__timeout: ${redis__liveUsuarios_timeout}
     idiomas: "es|Español,en|English"
     connectionType: "0"
     intervalo: "100"
    volumes:
     - ./logs/distributor:/app/logs
```

Se pueden consultar los posibles valores de configuración de cada parámetro aquí: https://github.com/equipognoss/Gnoss.SemanticAIPlatform.OpenCORE

## Código de conducta
Este proyecto a adoptado el código de conducta definido por "Contributor Covenant" para definir el comportamiento esperado en las contribuciones a este proyecto. Para más información ver https://www.contributor-covenant.org/

## Licencia
Este producto es parte de la plataforma [Gnoss Semantic AI Platform Open Core](https://github.com/equipognoss/Gnoss.SemanticAIPlatform.OpenCORE), es un producto open source y está licenciado bajo GPLv3.
