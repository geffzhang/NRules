# NRules Postgres Persistence sample

This sample stores serialized rule definitions in PostgreSQL and reloads the
compiled rules when the database reports a change.

## Start PostgreSQL

The sample uses a local database named `nrules_demo` and the demo container
name `nrules-pg`. Check that neither the container name nor host port `5432` is
already in use before starting it.

```sh
docker run -d --name nrules-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=nrules_demo postgres:16-alpine
until docker exec nrules-pg pg_isready -U postgres -d nrules_demo; do sleep 1; done
```

In PowerShell, run the same `docker run` command on one line, then wait for
readiness with:

```powershell
do {
    docker exec nrules-pg pg_isready -U postgres -d nrules_demo
    if ($LASTEXITCODE -ne 0) { Start-Sleep -Seconds 1 }
} while ($LASTEXITCODE -ne 0)
```

## Run

> `appsettings.json` contains the hard-coded password `postgres` for this
> local-only demo. Do not use this password or connection string in production.
> Override it with `ConnectionStrings__Rules` when using another local setup.

```sh
cd samples/Persistence.Postgres
dotnet run
```

The initial evaluation prints both rule activations:

```text
[VipOrderRule] 1
[LargeOrderRule] 1
```

## Demonstrate hot reload

In another terminal, update the stored priority. The trigger emits a
`rules_changed` notification; the reloader rebuilds the rule factory without
restarting the sample. Press Enter in the sample terminal to evaluate the new
factory:

```sh
docker exec -i nrules-pg psql -U postgres -d nrules_demo -c \
  "UPDATE rules SET priority = 99, rule_definition = jsonb_set(rule_definition, '{priority}', '99'::jsonb) WHERE name = 'LargeOrderRule';"
```
