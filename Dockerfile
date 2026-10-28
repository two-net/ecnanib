# The runtime image: the publish output of .github/workflows/ci.yml on the ASP.NET Core runtime. Locally:
#   dotnet publish src/Ecnanib/Ecnanib.csproj --configuration Release --output publish && docker build -t ecnanib .
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ .

# appsettings.json listens on http://127.0.0.1:5207, which nothing outside the container can reach: listen on every
# interface on the base image's conventional port 8080 instead, and clear ASPNETCORE_HTTP_PORTS so Kestrel does not warn
# that the Kestrel section overrides it. The rest (Database__ConnectionString, Database__EncryptionKey, Server__PublicUrl, ...) comes from the
# environment at run time, as README.md describes.
ENV ASPNETCORE_HTTP_PORTS= \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080
EXPOSE 8080

# The non-root user the base image creates.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ecnanib.dll"]
