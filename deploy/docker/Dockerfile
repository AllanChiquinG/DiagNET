# DiagNET — imagen de la aplicación (API + cliente Blazor). Contexto de construcción: la RAÍZ del repositorio.
#   docker build -f deploy/docker/Dockerfile -t diagnet:e11 .
# Versiones FIJADAS (etiquetas existentes comprobadas el 22/10/2026 en mcr.microsoft.com); no se usa «latest».
# Las etiquetas no se han fijado por digest: hacerlo (sha256) es un paso de endurecimiento previo a publicar.

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS compilacion
WORKDIR /src
COPY global.json Directory.Build.props DiagNET.slnx ./
COPY src ./src
# Release: recorta el cliente y no incluye appsettings.Local.json (los secretos nunca entran en la imagen).
RUN dotnet publish src/Servidor.Api -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS final
WORKDIR /app
COPY --from=compilacion /app ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# Sin privilegios de administrador dentro del contenedor.
USER $APP_UID
ENTRYPOINT ["dotnet", "Servidor.Api.dll"]
