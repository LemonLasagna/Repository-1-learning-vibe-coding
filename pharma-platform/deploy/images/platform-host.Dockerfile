# syntax=docker/dockerfile:1
# PharmaOS platform host: the API and the browser session (BFF). Build context: the pharma-platform folder.
#   docker build -f deploy/images/platform-host.Dockerfile -t pharmaos/platform-host:dev .
# Base images are named by tag for readability. Production pins them by digest (IQ-INF-002), when the release bundle is built.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# global.json pins the SDK band; the props files hold the shared settings and package versions.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/Hosts/PharmaOS.Platform.Host/PharmaOS.Platform.Host.csproj \
      --configuration Release --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Not root: the aspnet image ships an unprivileged user named "app".
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
EXPOSE 8080
# The image has no curl; /health/live is probed with bash's own TCP support.
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "PharmaOS.Platform.Host.dll"]
