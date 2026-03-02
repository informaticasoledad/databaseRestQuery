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
      "Connstr": "Driver={IBM i Access ODBC Driver};System=<iseries-host>;Database=<iseries-db>;Uid={{DB_USER}};Pwd={{DB_PWD}};Naming=0;DefaultLibraries=<default-lib>;"
    },
    {
      "ConnectionName": "genesserver2",
      "Type": "sqlserver",
      "Connstr": "Server=<sql-host>;Database=<database>;User Id={{DB_USER}};Password={{DB_PWD}};Encrypt=True;TrustServerCertificate=True;"
    }
  ],
  "S3Export": {
    "Enabled": true,
    "EndpointUrl": "https://s3.wasabisys.com",
    "Region": "us-east-1",
    "AccessKey": "{{S3_ACCESS_KEY}}",
    "SecretKey": "{{S3_SECRET_KEY}}",
    "Bucket": "<bucket-name>",
    "KeyPrefix": "database-rest-query/exports",
    "ForcePathStyle": true,
    "PresignedUrlMinutes": 60
  }
}
```

Variables sensibles por entorno:
- Crea/edita `./.env` con credenciales de DB y S3/Wasabi.
- Para shell local: `set -a; source .env; set +a`
- Para Docker: `--env-file .env`
- La API sustituye placeholders `{{VAR}}` en `appsettings.json` con variables de entorno.
- Minimo recomendado: `DB_USER`, `DB_PWD`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`.

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
- `S3Export`: destino para exportacion asincrona de resultados grandes (S3/Wasabi).

## Cadenas de conexion tipo por motor

Estas plantillas sirven para `server.connstr` (o `ServerConnections[].Connstr`):

### PostgreSQL (`server.type=postgresql`)

```text
Host=<pg-host>;Port=5432;Database=<database>;Username={{DB_USER}};Password={{DB_PWD}};Pooling=true;Timeout=15;Command Timeout=30;
```

### SQL Server moderno (`server.type=sqlserver`)

```text
Server=<sql-host>,1433;Database=<database>;User Id={{DB_USER}};Password={{DB_PWD}};Encrypt=True;TrustServerCertificate=True;Application Name=DatabaseRestQuery;
```

### SQL Server legacy por ODBC (`server.type=sqlserver_legacy`)

```text
Driver={ODBC Driver 17 for SQL Server};Server=<sql-host>,1433;Database=<database>;Uid={{DB_USER}};Pwd={{DB_PWD}};Encrypt=yes;TrustServerCertificate=yes;
```

### SQL Server legacy con FreeTDS (`server.type=freetds`)

```text
Driver=FreeTDS;Server=<sql-host>;Port=1433;Database=<database>;Uid={{DB_USER}};Pwd={{DB_PWD}};TDS_Version=7.2;ClientCharset=UTF-8;
```

### MySQL / MariaDB (`server.type=mysql`)

```text
Server=<mysql-host>;Port=3306;Database=<database>;User ID={{DB_USER}};Password={{DB_PWD}};Pooling=true;MinimumPoolSize=0;MaximumPoolSize=50;ConnectionTimeout=15;DefaultCommandTimeout=30;
```

### IBM i DB2 por ODBC (`server.type=db2-iseries` o `db2_iseries`)

```text
Driver={IBM i Access ODBC Driver};System=<iseries-host>;Database=<iseries-db>;Uid={{DB_USER}};Pwd={{DB_PWD}};Naming=0;DefaultLibraries=<default-lib>;CONNTYPE=1;
```

Notas:
- Usa variables de entorno (`{{DB_USER}}`, `{{DB_PWD}}`) y evita credenciales en claro en git.
- Para SQL Server antiguo, suele funcionar mejor `sqlserver_legacy` o `freetds`.
- En IBM i, `CONNTYPE=1` suele usarse para lectura y `CONNTYPE=0` para escritura.

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

Coloca los drivers Linux de IBM i en `docker/ibmi-odbc/`.

### macOS (Docker Desktop, imagen x86)

1. Purga de datos antiguos (cola SQLite local e imagen previa):

```bash
rm -f DatabaseRestQuery.Api/Data/queue.db DatabaseRestQuery.Api/Data/queue.db-shm DatabaseRestQuery.Api/Data/queue.db-wal
docker image rm -f database-rest-query:macos-x86 2>/dev/null || true
```

Opcional (limpieza agresiva de cache de build):

```bash
docker builder prune -f
```

2. Creacion de imagen:

```bash
docker buildx build \
  --platform linux/amd64 \
  -f Dockerfile.macos \
  --build-arg IBMI_IACCESS_INSTALL_FROM_APT=true \
  --build-arg IBMI_IACCESS_APT_PACKAGE=ibm-iaccess \
  -t database-rest-query:macos-x86 \
  --load \
  .
```

3. Levantar contenedor:

```bash
docker run --rm --platform linux/amd64 --env-file .env -p 8080:8080 \
  database-rest-query:macos-x86
```

### Linux

1. Purga de datos antiguos (cola SQLite local e imagen previa):

```bash
rm -f DatabaseRestQuery.Api/Data/queue.db DatabaseRestQuery.Api/Data/queue.db-shm DatabaseRestQuery.Api/Data/queue.db-wal
docker image rm -f database-rest-query:linux 2>/dev/null || true
```

Opcional (limpieza agresiva de cache de build):

```bash
docker builder prune -f
```

2. Creacion de imagen:

```bash
docker build -f Dockerfile -t database-rest-query:linux .
```

3. Levantar contenedor:

```bash
docker run --rm --env-file .env -p 8080:8080 \
  database-rest-query:linux
```

API en `http://localhost:8080`.

### Soporte SQL Server legacy con FreeTDS (fallback)

El Dockerfile instala `tdsodbc` y `freetds-bin` para permitir fallback de SQL Server antiguo.

Ejemplo de request para SQL Server legacy:

```json
{
    "server": {
      "type": "sqlserver_legacy",
      "connstr": "Driver=FreeTDS;Server=<sql-host>;Port=1433;Database=<database>;Uid=<db-user>;Pwd=<db-password>;TDS_Version=7.2;"
    },
  "transactionId": "tx-legacy-001",
  "query": "select getdate() as fecha",
  "useQueue": false
}
```

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
  "responseFormat": "json|jsonl|csv_tab|csv_comma|csv_pipeline|xml|toon|html_table",
  "responseQueueCallback": "https://mi-sistema/callback",
  "exportToS3": false,
  "exportFormat": "jsonl",
  "exportCompress": true
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
- `responseFormat` soporta: `json`, `jsonl`, `xml`, `toon`, `html_table`, `csv_tab`, `csv_comma`, `csv_pipeline`.
- El valor predeterminado de `responseFormat` es `json`.
- `responseQueueCallback` es opcional y solo aplica a peticiones encoladas: al finalizar, el worker envía `POST` con la respuesta a esa URL.
- `exportToS3=true` exporta el resultado a bucket S3/Wasabi y devuelve metadata+URL temporal.
- `exportFormat` soporta: `json`, `jsonl`, `csv_tab`, `csv_comma`, `csv_pipeline`.
- `exportCompress=true` comprime el archivo exportado en `gzip`.

Response:

```json
{
  "transactionId": "...",
  "ok": true,
  "message": "...",
  "result": [],
  "compressedResult": null,
  "export": {
    "provider": "s3",
    "bucket": "mi-bucket",
    "objectKey": "database-rest-query/exports/2026/02/27/tx_xxx.jsonl.gz",
    "url": "https://....",
    "urlExpiresAtUtc": "2026-02-27T12:00:00Z",
    "format": "jsonl",
    "compressed": true,
    "sizeBytes": 12345
  }
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

Con `responseFormat=jsonl`:
- La respuesta se entrega como `application/x-ndjson`.
- Cada fila se emite como una linea JSON.

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
    "connstr": "Driver={IBM i Access ODBC Driver};System=<iseries-host>;Database=<rdb>;Uid=<db-user>;Pwd=<db-password>;Naming=0;DefaultLibraries=<default-lib>;"
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
- Se aplica validacion de entrada (tipo de servidor, limites de timeout, cantidad de parametros y tamano de query/comando).
- La cola de respuestas se purga automaticamente por tiempo y por capacidad antes de encolar, para evitar llegar al limite configurado.
- Existe idempotencia fuerte para colas: mismo `transactionId` con payload distinto devuelve conflicto.
