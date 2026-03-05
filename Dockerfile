FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DatabaseRestQuery.sln ./
COPY DatabaseRestQuery.Api/DatabaseRestQuery.Api.csproj DatabaseRestQuery.Api/
RUN dotnet restore DatabaseRestQuery.Api/DatabaseRestQuery.Api.csproj

COPY . .
RUN dotnet publish DatabaseRestQuery.Api/DatabaseRestQuery.Api.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG IBMI_ODBC_URL=""
ARG IBMI_IACCESS_DEB_URL=""
ARG IBMI_IACCESS_INSTALL_FROM_APT="false"
ARG IBMI_IACCESS_APT_PACKAGE="ibm-iaccess"
ARG IBMI_IACCESS_APT_LIST_URL="https://public.dhe.ibm.com/software/ibmi/products/odbc/debs/dists/1.1.0/ibmi-acs-1.1.0.list"
COPY docker/ibmi-odbc/ /opt/ibm/iaccess/

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      freetds-bin \
      libxml2 \
      tdsodbc \
      unzip \
      unixodbc \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    if [ "$IBMI_IACCESS_INSTALL_FROM_APT" = "true" ]; then \
      curl -fsSL "$IBMI_IACCESS_APT_LIST_URL" -o /etc/apt/sources.list.d/ibmi-acs.list; \
      apt-get update; \
      if ! apt-get install -y --no-install-recommends "$IBMI_IACCESS_APT_PACKAGE"; then \
        echo "No se pudo instalar $IBMI_IACCESS_APT_PACKAGE via apt. Verifica repositorio IBM i Access para tu distro." >&2; \
        exit 1; \
      fi; \
      rm -rf /var/lib/apt/lists/*; \
    fi; \
    if [ -n "$IBMI_IACCESS_DEB_URL" ]; then \
      curl -fL "$IBMI_IACCESS_DEB_URL" -o /tmp/ibm-iaccess.deb; \
      apt-get update; \
      apt-get install -y --no-install-recommends /tmp/ibm-iaccess.deb; \
      rm -f /tmp/ibm-iaccess.deb; \
      rm -rf /var/lib/apt/lists/*; \
    fi

RUN sed -ri 's/^CipherString[[:space:]]*=.*/CipherString = DEFAULT@SECLEVEL=1/' /etc/ssl/openssl.cnf || true

RUN set -eux; \
    FREETDS_DRIVER_PATH="$(odbcinst -q -d -n 'FreeTDS' >/dev/null 2>&1 && odbcinst -q -d -n 'FreeTDS' | sed -n 's/^Driver[[:space:]]*=[[:space:]]*//p' | head -n 1 || true)"; \
    if [ -z "${FREETDS_DRIVER_PATH:-}" ]; then \
      FREETDS_DRIVER_PATH="/usr/lib/x86_64-linux-gnu/odbc/libtdsodbc.so"; \
      if [ ! -f "$FREETDS_DRIVER_PATH" ]; then \
        FREETDS_DRIVER_PATH="/usr/lib/aarch64-linux-gnu/odbc/libtdsodbc.so"; \
      fi; \
    fi; \
    if [ -f "$FREETDS_DRIVER_PATH" ] && ! grep -q '^\[FreeTDS\]' /etc/odbcinst.ini 2>/dev/null; then \
      printf '%s\n' \
        '[FreeTDS]' \
        'Description=FreeTDS Driver for Linux & MSSQL' \
        "Driver=$FREETDS_DRIVER_PATH" \
        'UsageCount=1' >> /etc/odbcinst.ini; \
    fi

RUN set -eux; \
    if [ -n "$IBMI_ODBC_URL" ]; then \
      mkdir -p /tmp/ibmi-odbc /opt/ibm/iaccess; \
      curl -fL "$IBMI_ODBC_URL" -o /tmp/ibmi-odbc/acs.zip; \
      unzip -q /tmp/ibmi-odbc/acs.zip -d /tmp/ibmi-odbc/extracted; \
      DRIVER_PATH="$(find /tmp/ibmi-odbc/extracted -type f -name 'libdb2o.so' -o -type f -name 'libdb2o.so.1' | head -n 1)"; \
      if [ -z "$DRIVER_PATH" ]; then \
        echo "No se encontro libdb2o.so dentro del paquete ACS." >&2; \
        exit 1; \
      fi; \
      DRIVER_DIR="$(dirname "$DRIVER_PATH")"; \
      cp -a "$DRIVER_DIR"/. /opt/ibm/iaccess/; \
      rm -rf /tmp/ibmi-odbc; \
    fi; \
    chmod -R a+rX /opt/ibm/iaccess || true; \
    DB2O_PATH="$(find /opt/ibm/iaccess -type f -name 'libdb2o.so.1' -o -type f -name 'libdb2o.so' | head -n 1)"; \
    if [ -z "$DB2O_PATH" ]; then \
      echo "No se encontro libdb2o.so(.1) en /opt/ibm/iaccess." >&2; \
      exit 1; \
    fi; \
    DB2O_DIR="$(dirname "$DB2O_PATH")"; \
    if [ ! -f "$DB2O_DIR/libdb2o.so" ] && [ -f "$DB2O_DIR/libdb2o.so.1" ]; then \
      ln -sf "$DB2O_DIR/libdb2o.so.1" "$DB2O_DIR/libdb2o.so"; \
    fi; \
    if [ -f "$DB2O_PATH" ] && ! grep -q '^\[IBM i Access ODBC Driver\]' /etc/odbcinst.ini 2>/dev/null; then \
      printf '%s\n' \
        '[IBM i Access ODBC Driver]' \
        'Description=IBM i Access for Linux ODBC Driver' \
        "Driver=$DB2O_PATH" \
        "Setup=$DB2O_PATH" \
        'Threading=2' \
        'UsageCount=1' >> /etc/odbcinst.ini; \
    fi

RUN printf '%s\n' \
      '[ODBC Data Sources]' \
      'FreeTDS=FreeTDS Driver' \
      'IBM i Access ODBC Driver=IBM i Access ODBC Driver' > /etc/odbc.ini

RUN printf '%s\n' '/opt/ibm/iaccess/lib' '/opt/ibm/iaccess/lib/icc' '/opt/ibm/iaccess' > /etc/ld.so.conf.d/ibmi-iaccess.conf && ldconfig || true

ENV ASPNETCORE_URLS=http://+:8080
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8
ENV DB2CODEPAGE=1208
ENV LD_LIBRARY_PATH=/opt/ibm/iaccess/lib/icc:/opt/ibm/iaccess/lib:/opt/ibm/iaccess/icc:/opt/ibm/iaccess
EXPOSE 8080

COPY --from=build /app/publish .
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["dotnet", "DatabaseRestQuery.Api.dll"]
