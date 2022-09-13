FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine-amd64

WORKDIR /app

RUN dotnet new console --name LobiArchiver
RUN dotnet add LobiArchiver package Newtonsoft.Json
RUN dotnet add LobiArchiver package Npgsql
COPY src LobiArchiver
RUN dotnet build --configuration Release LobiArchiver

RUN apk add postgresql-client
COPY --chmod=744 wait-for-postgres.sh wait-for-postgres.sh
CMD sh -c "/app/wait-for-postgres.sh && /app/LobiArchiver/bin/Release/net6.0/LobiArchiver"
