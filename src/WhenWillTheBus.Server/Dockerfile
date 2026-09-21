# SPDX-License-Identifier: GPL-3.0-or-later
# Build from the REPOSITORY ROOT so the Core project is in context:
#   docker build -f src/WhenWillTheBus.Server/Dockerfile -t whenwillthebus .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/WhenWillTheBus.Core/WhenWillTheBus.Core.csproj src/WhenWillTheBus.Core/
COPY src/WhenWillTheBus.Server/WhenWillTheBus.Server.csproj src/WhenWillTheBus.Server/
RUN dotnet restore src/WhenWillTheBus.Server/WhenWillTheBus.Server.csproj

COPY src/WhenWillTheBus.Core/ src/WhenWillTheBus.Core/
COPY src/WhenWillTheBus.Server/ src/WhenWillTheBus.Server/
RUN dotnet publish src/WhenWillTheBus.Server/WhenWillTheBus.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Learned history lives here: about six school weeks of arrivals per run, and
# the route tracks the estimate depends on. Mount it, or the app relearns from
# nothing after every restart.
VOLUME /data
ENV WWTB_WheresTheBus__DataDirectory=/data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Not root. This process holds the family's WheresTheBus password and knows
# where a child is right now.
USER $APP_UID

# Probes itself: see the --healthcheck branch in Program.cs. The runtime image
# has no curl and no wget, so anything written against those silently reports
# unhealthy for ever.
HEALTHCHECK --interval=60s --timeout=10s --start-period=20s --retries=3 \
  CMD ["dotnet", "WhenWillTheBus.Server.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "WhenWillTheBus.Server.dll"]
