FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env

WORKDIR /app

# Copy everything
COPY RTInternetArchiveUploader/ ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out


# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0

WORKDIR /app

# Install IA
RUN apt update && \
    apt install -y curl python3 && \
    curl -LOs https://archive.org/download/ia-pex/ia && \
    mv ia /usr/local/bin && \
    chmod +x /usr/local/bin/ia

COPY --from=build-env /app/out .

ENTRYPOINT ["/app/RTInternetArchiveUploader"]