#!/bin/sh
set -eu

if [ "${DB2_VALIDATE_ON_START:-false}" = "true" ]; then
  DB2_VALIDATE_MODE="${DB2_VALIDATE_MODE:-auto}"
  DB2_VALIDATE_FAIL_ON_ERROR="${DB2_VALIDATE_FAIL_ON_ERROR:-true}"

  run_db2cli_validate() {
    DB2CLI_BIN="${DB2CLI_BIN:-/opt/ibm/iaccess/bin/db2cli}"
    DB2_LICENSE_DIR="${DB2_LICENSE_DIR:-/opt/ibm/iaccess/license}"

    if [ ! -x "$DB2CLI_BIN" ]; then
      echo "DB2 validate (db2cli) habilitado, pero no existe ejecutable en: $DB2CLI_BIN" >&2
      return 1
    fi

    if [ -z "${DB2_VALIDATE_DSN:-}" ] || [ -z "${DB2_VALIDATE_USER:-}" ] || [ -z "${DB2_VALIDATE_PASSWORD:-}" ]; then
      echo "DB2 validate (db2cli) requiere DB2_VALIDATE_DSN, DB2_VALIDATE_USER y DB2_VALIDATE_PASSWORD." >&2
      return 1
    fi

    if ! find "$DB2_LICENSE_DIR" -maxdepth 1 -type f -name '*.lic' | grep -q .; then
      echo "No se encontro archivo .lic en $DB2_LICENSE_DIR. Copia la licencia Db2 Connect para evitar SQL1598N." >&2
      return 1
    fi

    echo "Ejecutando db2cli validate para DSN: ${DB2_VALIDATE_DSN}"
    validate_output="$(mktemp)"
    if ! "$DB2CLI_BIN" validate -dsn "$DB2_VALIDATE_DSN" -connect -user "$DB2_VALIDATE_USER" -passwd "$DB2_VALIDATE_PASSWORD" >"$validate_output" 2>&1; then
      cat "$validate_output"
      rm -f "$validate_output"
      return 1
    fi

    cat "$validate_output"
    if grep -q "\\[FAILED\\]" "$validate_output"; then
      rm -f "$validate_output"
      return 1
    fi

    rm -f "$validate_output"
    return 0
  }

  run_odbc_validate() {
    if [ -n "${DB2_VALIDATE_CONNSTR:-}" ]; then
      connstr="$DB2_VALIDATE_CONNSTR"
    else
      if [ -z "${DB2_VALIDATE_SYSTEM:-}" ] || [ -z "${DB2_VALIDATE_USER:-}" ] || [ -z "${DB2_VALIDATE_PASSWORD:-}" ]; then
        echo "DB2 validate (odbc) requiere DB2_VALIDATE_CONNSTR o DB2_VALIDATE_SYSTEM+DB2_VALIDATE_USER+DB2_VALIDATE_PASSWORD." >&2
        return 1
      fi
      driver="${DB2_VALIDATE_DRIVER:-IBM i Access ODBC Driver}"
      naming="${DB2_VALIDATE_NAMING:-0}"
      connstr="Driver={${driver}};System=${DB2_VALIDATE_SYSTEM};Uid=${DB2_VALIDATE_USER};Pwd=${DB2_VALIDATE_PASSWORD};Naming=${naming};"
      if [ -n "${DB2_VALIDATE_DEFAULT_LIBRARIES:-}" ]; then
        connstr="${connstr}DefaultLibraries=${DB2_VALIDATE_DEFAULT_LIBRARIES};"
      fi
    fi

    echo "Ejecutando validate ODBC con isql"
    validate_output="$(mktemp)"
    if ! isql -v -k "$connstr" >"$validate_output" 2>&1; then
      cat "$validate_output"
      rm -f "$validate_output"
      return 1
    fi

    cat "$validate_output"
    rm -f "$validate_output"
    return 0
  }

  mode="$DB2_VALIDATE_MODE"
  if [ "$mode" = "auto" ]; then
    if [ -x "${DB2CLI_BIN:-/opt/ibm/iaccess/bin/db2cli}" ] && [ -n "${DB2_VALIDATE_DSN:-}" ]; then
      mode="db2cli"
    else
      mode="odbc"
    fi
  fi

  case "$mode" in
    db2cli)
      if ! run_db2cli_validate; then
        if [ "$DB2_VALIDATE_FAIL_ON_ERROR" = "true" ]; then
          echo "db2cli validate fallo y DB2_VALIDATE_FAIL_ON_ERROR=true. Abortando inicio." >&2
          exit 1
        fi
        echo "db2cli validate fallo, pero DB2_VALIDATE_FAIL_ON_ERROR=false. Continuando inicio." >&2
      fi
      ;;
    odbc)
      if ! run_odbc_validate; then
        if [ "$DB2_VALIDATE_FAIL_ON_ERROR" = "true" ]; then
          echo "ODBC validate fallo y DB2_VALIDATE_FAIL_ON_ERROR=true. Abortando inicio." >&2
          exit 1
        fi
        echo "ODBC validate fallo, pero DB2_VALIDATE_FAIL_ON_ERROR=false. Continuando inicio." >&2
      fi
      ;;
    *)
      echo "DB2_VALIDATE_MODE invalido: $mode. Usa auto|odbc|db2cli." >&2
      exit 1
      ;;
  esac
fi

exec "$@"
