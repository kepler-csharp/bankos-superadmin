 

# Etapa 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copiar csproj y restaurar dependencias
COPY *.csproj ./
RUN dotnet restore

# Copiar todo el código
COPY . ./

# Publicar la aplicación
RUN dotnet publish -c Release -o /out

# Etapa 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Copiar desde la etapa build
COPY --from=build /out .

# Exponer puerto
EXPOSE 8080

# Comando de inicio
ENTRYPOINT ["dotnet", "BankOsAdmin.dll"]