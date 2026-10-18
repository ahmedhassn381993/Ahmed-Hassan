# Container image (on-premises hosting or Azure Container Apps); App Service can also run the zip from deploy/publish.sh.
FROM node:22-alpine AS screens
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npx tsc -b && npx vite build --outDir /out/wwwroot --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/Enppi.Workload.Api -c Release -o /app --nologo
COPY --from=screens /out/wwwroot /app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /app .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Enppi.Workload.Api.dll"]
