# DatabaseRestQuery

API REST en .NET 10 para ejecutar consultas SQL de forma directa o encolada con cola embebida en SQLite.

## Caracteristicas

- Endpoint `doQuery` para ejecutar consultas/comandos.
- Modo directo (`useQueue=false`) o encolado (`useQueue=true`).
- Cola persistente en SQLite (`Data/queue.db`).
- Workers configurables por `appsettings.json`.
- Endpoints para seguimiento y limpieza de cola.
- Soporte de gestores: `sqlserver`, `sqlserver-legacy`/`freetds`, `postgresql`, `mysql`, `db2-iseries`/`db2_iseries`.
- Despliegue local o con Docker.

## Requisitos

- .NET SDK 10.0+
- Para `db2-iseries`: driver ODBC instalado en el host/contenedor.

## Configuracion

Archivo: `DatabaseRestQuery.Api/appsettings.json`

```json
{
  "Queue": {
    "RunMode": "All",
    "DbPath": "Data/queue.db",
    "WorkersCount": 2,
    "PollIntervalMs": 500,
    "WaitPollIntervalMs": 500,
    "BackpressureMaxInFlight": 5000,
    "EnablePreparedStatements": true,
    "EnableCircuitBreaker": true,
    "CircuitBreakerFailureThreshold": 5,
    "CircuitBreakerOpenSeconds": 30,
    "EnablePartitionSharding": true,
    "EnableBufferedEnqueue": true,
    "EnqueueBufferCapacity": 5000,
    "EnqueueFlushIntervalMs": 200,
    "EnqueueFlushBatchSize": 100,
    "MaxRetries": 3,
    "RetryDelaySeconds": 5,
    "ProcessingLeaseSeconds": 120,
    "CleanupIntervalSeconds": 60,
    "CompletedRetentionHours": 24,
    "ResponseRetentionHours": 24,
    "ResponseQueueMaxItems": 10000,
    "ResponseQueueTargetItemsAfterPurge": 9000,
    "MaxRowsLimit": 10000,
    "MaxCommandTextLength": 100000,
    "MaxParamsCount": 200,
    "MaxExecutionTimeoutSeconds": 600,
    "MaxCommandTimeoutSeconds": 600
  },
  "ServerConnections": [
    {
      "ConnectionName": "as400",
      "Type": "db2-iseries",
      "Connstr": "Driver={IBM i Access ODBC Driver};System=192.168.38.2;Database=S786C9A1;Uid=usuario;Pwd=clave;Naming=0;DefaultLibraries=NSOL001;"
    },
    {
      "ConnectionName": "genesserver2",
      "Type": "sqlserver",
      "Connstr": "Server=genesserver2;Database=mi_bd;User Id=usuario;Password=clave;Encrypt=True;TrustServerCertificate=True;"
    }
  ]
}
```

Significado rapido:
- `RunMode`: `All`, `Api`, `Worker` para separar API y workers en despliegues independientes.
- `BackpressureMaxInFlight`: limite de `Pending+Processing` para devolver `429` y proteger el sistema.
- `EnablePreparedStatements`: intenta preparar comandos parametrizados.
- `EnableCircuitBreaker`: abre circuito por datasource tras fallos consecutivos.
- `EnablePartitionSharding`: reparte particiones entre workers para aislar carga por origen.
- `EnableBufferedEnqueue`: buffer en memoria con persistencia diferida para encolado asíncrono.
- `MaxRetries`: intentos maximos para jobs encolados (incluye intento final que puede quedar en `Failed`).
- `RetryDelaySeconds`: espera entre reintentos.
- `ProcessingLeaseSeconds`: si un worker se cae y deja un job en `Processing`, se recupera pasado este tiempo.
- `CleanupIntervalSeconds`: frecuencia de limpieza de jobs `Completed/Failed`.
- `ResponseRetentionHours`: tiempo maximo de retencion de respuestas (`Completed/Failed`).
- `ResponseQueueMaxItems`: tamano maximo de la cola de respuestas en SQLite.
- `ResponseQueueTargetItemsAfterPurge`: objetivo tras purga automatica por capacidad (debe ser menor que `ResponseQueueMaxItems`).
- `CompletedRetentionHours`: compatibilidad con versiones previas; se toma el menor valor entre este y `ResponseRetentionHours`.
- `Max*`: limites de validacion para proteger el servicio.

## Ejecutar local

```bash
dotnet restore
dotnet run --project DatabaseRestQuery.Api
```

API por defecto en `http://localhost:5000` (o el puerto asignado por `launchSettings`/ASPNETCORE_URLS).

### Panel web de control

La API incluye una web estatica en la raiz `/` con 3 areas:
- Estado operativo: cola, trabajos pendientes, metricas y purga.
- Historial de ultimas peticiones y ultimas respuestas (incluye contenido de respuesta).
- Pruebas de endpoints: cliente integrado para invocar la API.
- Endpoints y uso: referencia tipo swagger y visualizacion de `/openapi/v1.json`.

### Ejecutar API y Worker por separado (misma imagen/binario)

```bash
# Instancia API (sin workers)
Queue__RunMode=Api dotnet run --project DatabaseRestQuery.Api

# Instancia Worker (sin endpoints de negocio)
Queue__RunMode=Worker dotnet run --project DatabaseRestQuery.Api
```

## Ejecutar con Docker

El `Dockerfile` es multi-arquitectura y funciona con imagenes Linux `amd64` y `arm64` (incluyendo Docker Desktop en macOS Intel y Apple Silicon).

```bash
docker build -t database-rest-query .
docker run --rm -p 8080:8080 database-rest-query
```

API en `http://localhost:8080`.

### Dockerfile especifico para macOS host

Tambien tienes `Dockerfile.macos` para builds hechas desde macOS con carpeta local de drivers:
- `docker/ibmi-odbc/`

Comando:

```bash
docker build -f Dockerfile.macos -t database-rest-query:macos .
docker run --rm -p 8080:8080 database-rest-query:macos
```

Nota importante:
- Docker Desktop en macOS ejecuta contenedores Linux. Debes colocar drivers Linux en `docker/ibmi-odbc/` aunque el host sea macOS.

### Incluir driver IBM i ODBC en la imagen

El Dockerfile ya instala `unixODBC`.

Opcion recomendada: copiar los binarios del driver dentro del proyecto en:
- `docker/ibmi-odbc/`

Debe incluir `libdb2o.so` (y sus dependencias) para que durante `docker build` se copie a `/opt/ibm/iaccess` y se registre automaticamente `IBM i Access ODBC Driver`.

Si no incluyes esos binarios en `docker/ibmi-odbc/`, puedes usar fallback por URL ACS en build time:

```bash
docker build \
  --build-arg IBMI_ODBC_URL="https://<url-del-zip-de-acs>" \
  -t database-rest-query:ibmi .
```

Alternativa recomendada para ACS real en Linux: instalar paquete `.deb` durante build:

```bash
docker buildx build \
  --platform linux/amd64 \
  -f Dockerfile.macos \
  --build-arg IBMI_IACCESS_DEB_URL="https://<url-directa>/ibm-iaccess_<version>_amd64.deb" \
  -t database-rest-query:macos-x86 \
  --load \
  .
```

Si tu imagen base ya tiene configurado el repositorio IBM i Access, tambien puedes instalar por `apt`:

```bash
docker buildx build \
  --platform linux/amd64 \
  -f Dockerfile.macos \
  --build-arg IBMI_IACCESS_INSTALL_FROM_APT=true \
  --build-arg IBMI_IACCESS_APT_PACKAGE=ibm-iaccess \
  --build-arg IBMI_IACCESS_APT_LIST_URL="https://public.dhe.ibm.com/software/ibmi/products/odbc/debs/dists/1.1.0/ibmi-acs-1.1.0.list" \
  -t database-rest-query:macos-x86 \
  --load \
  .
```

Esto registra el driver en `/etc/odbcinst.ini` con el nombre:
- `IBM i Access ODBC Driver`

#### Que es la URL de ACS

- `ACS` significa `IBM i Access Client Solutions`.
- `IBMI_ODBC_URL` es una URL directa al `.zip` oficial de ACS que contiene el driver Linux (`libdb2o.so`).
- Esa URL normalmente se obtiene desde el portal oficial de IBM (puede requerir login/licencia).

Ejemplo de uso del argumento:

```bash
--build-arg IBMI_ODBC_URL="https://servidor/ibm/acs/ibm-iaccess-acs.zip"
```

Verificacion rapida dentro del contenedor:

```bash
odbcinst -q -d
```

Debe aparecer `IBM i Access ODBC Driver`.

Validacion opcional al iniciar el contenedor (ACS ODBC con `isql`):

```bash
docker run --rm -p 8080:8080 \
  -e DB2_VALIDATE_ON_START=true \
  -e DB2_VALIDATE_MODE=odbc \
  -e DB2_VALIDATE_SYSTEM=192.168.38.2 \
  -e DB2_VALIDATE_USER=QSOLAP_001 \
  -e DB2_VALIDATE_PASSWORD=INFQS99999 \
  -e DB2_VALIDATE_DEFAULT_LIBRARIES=NSOL001 \
  -e DB2_VALIDATE_FAIL_ON_ERROR=true \
  database-rest-query:macos-x86
```

Variables:
- `DB2_VALIDATE_ON_START=true|false`: habilita validacion en startup.
- `DB2_VALIDATE_MODE=auto|odbc|db2cli`: para ACS usa `odbc`.
- `DB2_VALIDATE_SYSTEM`, `DB2_VALIDATE_USER`, `DB2_VALIDATE_PASSWORD`: usados por validate ODBC.
- `DB2_VALIDATE_DEFAULT_LIBRARIES`: opcional para validate ODBC.
- `DB2_VALIDATE_CONNSTR`: opcional; si se define, se usa connstr completa en lugar de construirla.
- `DB2_VALIDATE_FAIL_ON_ERROR=true|false`: si falla validate, aborta o continua inicio.

Build y ejecucion recomendada en x86 (ACS + validate ODBC):

```bash
docker buildx build \
  --platform linux/amd64 \
  -f Dockerfile.macos \
  --build-arg IBMI_IACCESS_INSTALL_FROM_APT=true \
  --build-arg IBMI_IACCESS_APT_PACKAGE=ibm-iaccess \
  --build-arg IBMI_IACCESS_APT_LIST_URL="https://public.dhe.ibm.com/software/ibmi/products/odbc/debs/dists/1.1.0/ibmi-acs-1.1.0.list" \
  -t database-rest-query:macos-x86 \
  --load \
  .

docker run --rm \
  --platform linux/amd64 \
  -p 8080:8080 \
  -e DB2_VALIDATE_ON_START=true \
  -e DB2_VALIDATE_MODE=odbc \
  -e DB2_VALIDATE_SYSTEM=192.168.38.2 \
  -e DB2_VALIDATE_USER=QSOLAP_001 \
  -e DB2_VALIDATE_PASSWORD=INFQS99999 \
  -e DB2_VALIDATE_DEFAULT_LIBRARIES=NSOL001 \
  -e DB2_VALIDATE_FAIL_ON_ERROR=true \
  database-rest-query:macos-x86
```

### Soporte SQL Server legacy con FreeTDS (fallback)

El Dockerfile instala `tdsodbc` y `freetds-bin` para permitir fallback de SQL Server antiguo.

Ejemplo de request para SQL Server legacy:

```json
{
  "server": {
    "type": "sqlserver_legacy",
    "connstr": "Driver=FreeTDS;Server=genesserver2.gruposoledad.com;Port=1433;Database=soledadbo;Uid=usuario;Pwd=clave;TDS_Version=7.2;"
  },
  "transactionId": "tx-legacy-001",
  "query": "select getdate() as fecha",
  "useQueue": false
}
```

### Build multi-plataforma (linux/amd64 + linux/arm64)

```bash
docker buildx create --use --name multiarch-builder
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --build-arg IBMI_ODBC_URL="https://<url-del-zip-de-acs>" \
  -t database-rest-query:latest \
  --load \
  .
```

Si quieres publicar en registry, usa `--push` en lugar de `--load`.

## Endpoints

### 1) POST `/doQuery`

Request:

```json
{
  "connectionName": "as400",
  "server": {
    "type": "sqlserver|sqlserver_legacy|freetds|postgresql|mysql|db2_iseries|db2-iseries",
    "connstr": "cadena de conexion"
  },
  "transactionId": "identificador-opcional",
  "query": "consulta opcional",
  "command": {
    "commandTimeout": 30,
    "commandText": "consulta/comando opcional",
    "params": [
      { "name": "@id", "value": 10 }
    ]
  },
  "executionTimeout": 30,
  "rowsLimit": 0,
  "waitForResponse": true,
  "useQueue": true,
  "compressResult": false,
  "streamResult": false,
  "queuePartition": "tenant-a",
  "responseFormat": "json",
  "responseQueueCallback": "https://mi-sistema/callback"
}
```

Notas:

- Si falta `transactionId`, la API genera uno.
- Puedes enviar `connectionName` en lugar de `server` para usar una conexion definida en `ServerConnections`.
- Si envias ambos (`connectionName` y `server`), se prioriza `server`.
- Para SQL Server antiguo (ej. 2012), usa `server.type=sqlserver_legacy` o `freetds` con cadena ODBC FreeTDS.
- Se usa `command.commandText` si existe; en caso contrario, `query`.
- `rowsLimit=0` significa sin limite.
- `useQueue=true` encola la solicitud.
- Si `useQueue=true` y `waitForResponse=true`, la API espera hasta `executionTimeout` segundos por resultado.
- Si `compressResult=true`, el resultado se devuelve comprimido en ZIP y codificado en Base64.
- Si `streamResult=true` (solo con `useQueue=false`), la respuesta se transmite en streaming JSON.
- `queuePartition` permite particionar la cola por cliente/origen/tenant.
- `responseFormat` soporta: `json`, `xml`, `toon`, `html_table`, `csv_tab`, `csv_comma`, `csv_pipeline`.
- El valor predeterminado de `responseFormat` es `json`.
- `responseQueueCallback` es opcional y solo aplica a peticiones encoladas: al finalizar, el worker envía `POST` con la respuesta a esa URL.

Response:

```json
{
  "transactionId": "...",
  "ok": true,
  "message": "...",
  "result": [],
  "compressedResult": null
}
```

Ejemplo de llamada usando `connectionName`:

```bash
curl -X POST http://localhost:8080/doQuery \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "as400",
    "transactionId": "tx-connname-001",
    "command": {
      "commandTimeout": 10,
      "commandText": "select * from NSOL001.CLNCL fetch first 100 rows only",
      "params": []
    },
    "executionTimeout": 30,
    "rowsLimit": 100,
    "waitForResponse": true,
    "useQueue": false
  }'
```

Cuando `compressResult=true` y hay filas, la respuesta retorna:
- `result: []`
- `compressedResult: "<base64 del zip con result.json>"`

Con `responseFormat=csv_tab|csv_comma|csv_pipeline`:
- La respuesta se entrega como fichero CSV.
- La primera fila contiene nombres de columnas.
- Separador de columnas: tab, coma o `|`.

Con `responseFormat=html_table`:
- La respuesta se entrega como HTML con tabla (`text/html`).

## Ejemplos de cadenas de conexion

### sqlserver (SqlClient)

```text
Server=mi-servidor-sql,1433;Database=mi_bd;User Id=mi_usuario;Password=mi_password;Encrypt=True;TrustServerCertificate=True;
```

### sqlserver_legacy / freetds (ODBC + FreeTDS)

```text
Driver=FreeTDS;Server=mi-servidor-sql-legacy;Port=1433;Database=mi_bd;Uid=mi_usuario;Pwd=mi_password;TDS_Version=7.2;
```

### postgresql

```text
Host=mi-servidor-pg;Port=5432;Database=mi_bd;Username=mi_usuario;Password=mi_password;SSL Mode=Prefer;
```

### mysql

```text
Server=mi-servidor-mysql;Port=3306;Database=mi_bd;User ID=mi_usuario;Password=mi_password;SslMode=Preferred;
```

### db2-iseries (ODBC)

```text
Driver={IBM i Access ODBC Driver};System=mi-iseries-host;Database=S786C9A1;Uid=mi_usuario;Pwd=mi_password;Naming=0;DefaultLibraries=MI_LIB;
```

Ejemplo completo para `POST /doQuery` con IBM i Access ODBC:

```json
{
  "server": {
    "type": "db2-iseries",
    "connstr": "Driver={IBM i Access ODBC Driver};System=192.168.38.2;Database=S786C9A1;Uid=mi_usuario;Pwd=mi_password;Naming=0;DefaultLibraries=NSOL001;"
  },
  "transactionId": "tx-demo-acs-001",
  "command": {
    "commandTimeout": 10,
    "commandText": "select * from NSOL001.CLNCL fetch first 100 rows only",
    "params": []
  },
  "executionTimeout": 30,
  "rowsLimit": 100,
  "waitForResponse": true,
  "useQueue": false,
  "compressResult": false,
  "streamResult": false,
  "queuePartition": "tenant-demo"
}
```

### 2) GET `/checkResponse/{transactionId}`

Consulta estado/resultado de una peticion encolada.

### 3) GET `/queuePendingJobs`

Lista jobs en estado `Pending` o `Processing` incluyendo intentos y proximo reintento (`nextAttemptAt`).

### 4) POST `/queuePurge`

Elimina todos los jobs en estado `Pending`.

### 5) GET `/health`

Devuelve estado del servicio y estadisticas de cola (incluye `delayedRetry` para jobs en espera de reintento).

### 6) GET `/metrics`

Métricas en formato Prometheus/OpenTelemetry para latencias, ejecuciones DB, reintentos y rechazos por backpressure.

### 7) GET `/openapi/v1.json`

Definicion OpenAPI consumida por el panel de control.

### 8) GET `/historyRecent?limit=30`

Devuelve historial reciente de peticiones/respuestas para el panel de control.

## Ejemplo rapido con cURL

```bash
curl -X POST http://localhost:8080/doQuery \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "genesserver2",
    "transactionId": "tx-001",
    "command": {
      "commandText": "select now() as fecha",
      "commandTimeout": 10,
      "params": []
    },
    "executionTimeout": 30,
    "rowsLimit": 10,
    "waitForResponse": true,
    "useQueue": false
  }'
```

## Observaciones

- Las peticiones encoladas quedan persistidas en SQLite.
- `queuePurge` no elimina trabajos ya `Processing`, `Completed` o `Failed`.
- Para DB2 iSeries se usa ODBC; la cadena depende del driver instalado.
- El contenedor incluye `unixODBC` y soporta instalar el driver IBM i ACS via `IBMI_ODBC_URL`.
- Se aplica validacion de entrada (tipo de servidor, limites de timeout, cantidad de parametros y tamano de query/comando).
- La cola de respuestas se purga automaticamente por tiempo y por capacidad antes de encolar, para evitar llegar al limite configurado.
- Existe idempotencia fuerte para colas: mismo `transactionId` con payload distinto devuelve conflicto.
