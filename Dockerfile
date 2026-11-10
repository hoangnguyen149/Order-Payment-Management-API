# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/OrderPayment.Domain/OrderPayment.Domain.csproj src/OrderPayment.Domain/
COPY src/OrderPayment.Application/OrderPayment.Application.csproj src/OrderPayment.Application/
COPY src/OrderPayment.Infrastructure/OrderPayment.Infrastructure.csproj src/OrderPayment.Infrastructure/
COPY src/OrderPayment.Api/OrderPayment.Api.csproj src/OrderPayment.Api/
COPY src/OrderPayment.FakeExternalApi/OrderPayment.FakeExternalApi.csproj src/OrderPayment.FakeExternalApi/
# Optional combined CA bundle for managed cloud proxies; TLS verification stays enabled.
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; fi; \
    dotnet restore src/OrderPayment.Api/OrderPayment.Api.csproj && \
    dotnet restore src/OrderPayment.FakeExternalApi/OrderPayment.FakeExternalApi.csproj
COPY src/ src/
RUN dotnet publish src/OrderPayment.Api/OrderPayment.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false
RUN dotnet publish src/OrderPayment.FakeExternalApi/OrderPayment.FakeExternalApi.csproj -c Release --no-restore -o /app/fake /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderPayment.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS fake
WORKDIR /app
RUN mkdir -p /data && chown app:app /data
COPY --from=build /app/fake .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderPayment.FakeExternalApi.dll"]

FROM runtime AS api
