# Production image. Note what is absent: no nuget.config, no private feed, no PAT build
# args. Hephaisto references no internal package, which is why this Dockerfile takes no
# credentials at all - and therefore why the Tiltfile never has to default one.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Restore against the manifests alone so a source-only change reuses the layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Hephaisto.Core/Hephaisto.Core.csproj                     src/Hephaisto.Core/
COPY src/Hephaisto.ServiceDefaults/Hephaisto.ServiceDefaults.csproj src/Hephaisto.ServiceDefaults/
COPY src/Hephaisto.Agent/Hephaisto.Agent.csproj                   src/Hephaisto.Agent/
RUN dotnet restore src/Hephaisto.Agent/Hephaisto.Agent.csproj

# Only these three projects are copied. The AppHost is dev-time orchestration and the
# Simulator is a dev fault generator; neither belongs in the pod, and leaving them out is
# what guarantees no Aspire.Hosting.* assembly ships.
COPY src/Hephaisto.Core/          src/Hephaisto.Core/
COPY src/Hephaisto.ServiceDefaults/ src/Hephaisto.ServiceDefaults/
COPY src/Hephaisto.Agent/         src/Hephaisto.Agent/

# MinVer derives the version from git, and .dockerignore correctly excludes .git/ - so it
# cannot run in here, and left to itself it would stamp every image 0.0.0-alpha.0. The
# version is therefore computed ONCE outside and passed in:
#
#     V=$(dotnet minver -t v -p main.0)
#     docker build --build-arg VERSION=$V --build-arg COMMIT=$(git rev-parse HEAD) .
#
# `-p main.0` is not optional: minver-cli defaults to alpha.0 while this repo's MSBuild
# properties say main.0, so without it the CLI and the compiler disagree about what the same
# commit is called.
ARG VERSION=0.0.0-local
ARG COMMIT=unknown

# NO --no-restore HERE, AND IT MUST NOT COME BACK.
#
# The restore above runs when only the .csproj files exist - that is the whole point of the
# layer split - and at that moment this project contains no Razor components. The Blazor
# framework's static web assets are resolved from that restore, so they are simply absent, and
# `--no-restore` at publish reuses the incomplete result. The manifest ships without
# `_framework/blazor.web.js`, `@Assets[...]` falls through to the literal path, and the browser
# gets a 404.
#
# Nothing about that fails the build or logs anything. The console renders perfectly, because
# the static server-side render is unaffected - and then nothing on it works. Every button, the
# approval controls included, is dead in every released image. Measured:
#
#     --no-restore   42489 byte manifest, 0 entries matching blazor   -> 404
#     (restored)     56532 byte manifest, blazor.web.js present       -> 200
#
# The layer split still earns its keep: the packages are already in the image's NuGet cache, so
# the restore this publish performs is near-instant and downloads nothing.
RUN dotnet publish src/Hephaisto.Agent/Hephaisto.Agent.csproj \
        -c Release -o /app \
        -p:MinVerSkip=true \
        -p:Version=${VERSION} \
        -p:InformationalVersion=${VERSION}+${COMMIT}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# OCI metadata. `licenses` in particular: an image is the form most people will actually
# receive this program in, and AGPL-3.0 is not what a reader assumes by default.
ARG VERSION
ARG COMMIT
LABEL org.opencontainers.image.title="Hephaisto" \
      org.opencontainers.image.description="An autonomous SRE agent that investigates Kubernetes incidents." \
      org.opencontainers.image.source="https://github.com/TrueRelevance/hephaisto" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}"

# Non-root. The agent holds a ServiceAccount token that can delete pods in one namespace;
# there is no reason for the process to also be root inside its own container.
#
# The uid is set numerically and the files are chowned on the way in, rather than creating a
# named user: this base image ships neither adduser nor useradd (it is Ubuntu-derived but
# carries no shadow-utils), so `RUN adduser ...` fails with exit 127. Nothing needs a passwd
# entry - Kubernetes matches runAsUser: 64198 in infra/app/hephaisto.yaml by number, and a
# numeric USER still satisfies runAsNonRoot.
COPY --from=build --chown=64198:64198 /app .
USER 64198:64198

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "Hephaisto.Agent.dll"]
