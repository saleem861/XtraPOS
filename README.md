# XtraPOS

Point-of-sale application. The first launch sets up one business, one branch, the first cashier station, and this device. The browser talks only to the API. The API creates the SQL Server database `XtraPOS_Branch`.

## Layout

```
src/XtraPOS.Web/    React, TypeScript, Vite
src/XtraPOS.Api/    ASP.NET Core Web API
database/           Notes only. Do not put runtime database files here.
docs/               Step notes
```

## Run

Web (port 5173):

```
cd src/XtraPOS.Web
npm install
npm run dev
```

API (port 5080):

```
dotnet run --project src/XtraPOS.Api --launch-profile http
```

The web dev server proxies `/api` and `/health` to the API. Health check: `GET http://localhost:5080/health`

## SQL Server

SQL Server is assumed to be installed already. This step does not install SQL Server Express.

Development uses the local default instance and Windows authentication. No password is stored in the repository.

`src/XtraPOS.Api/appsettings.Development.json`:

```
SqlServer:Server = localhost
SqlServer:Database = XtraPOS_Branch
```

The API accepts only the database name `XtraPOS_Branch`. It will not open or create `saleemPOSDB`, `Shop1`, `Shop2`, `swstore`, or any other database.

Optional SQL authentication, if Windows authentication is not used, must come from the environment. Do not put the password in a file:

```
$env:SqlServer__User = "xtrapos_dev"
$env:SqlServer__Password = "<password>"
```

Leave both unset to keep Windows authentication. The API builds the connection string itself. The browser never receives it.

On first-run setup the API:

1. Connects to the `master` database.
2. Creates `XtraPOS_Branch` when it is missing, with collation `Arabic_CI_AS`.
3. Applies pending schema migrations.
4. Inserts the business, branch, station, and device in one transaction.

Restarting the API applies pending migrations only when `XtraPOS_Branch` already exists. It does not create another business, branch, station, or device.

SQL Server stores the database files in its own data directory, outside this OneDrive project.

## First launch

The screen title is إعداد XtraPOS.

- إنشاء محل / فرع جديد runs the setup wizard.
- الانضمام إلى فرع موجود is shown and disabled, labeled قريباً.

After setup, the browser keeps the device id and the one-time device token in local storage. SQL Server stores only the token hash. A registered device signs in against XtraPOS_Branch. The session is an HttpOnly cookie named XtraPOS.Auth. Passwords are stored with ASP.NET Core PasswordHasher and are not kept in the browser. Timestamps are UTC, truncated to seconds. Reloading does not create duplicate records. If a branch was created before the first administrator existed, the app asks for إنشاء حساب مدير النظام without recreating the branch.

## API

- `GET /health`
- `GET /api/setup/status` — `NotConfigured`, `Configuring`, `Configured`, or `Error`
- `POST /api/setup/create-branch` — newline-delimited progress, then the one-time device token
- `GET /api/device/current` — requires `X-Device-Id` and `X-Device-Token`
- `POST /api/device/heartbeat` — same device headers
- `POST /api/dev/reset` — development only, and only with an explicit confirmation body

## Development reset

In a development build, إعادة تعيين إعداد التطوير asks for confirmation and then drops only `XtraPOS_Branch`. The endpoint is not mapped outside Development. It is not part of the normal setup screen in a production build.

## Dr. Salim

`dr-salim/` is a separate perfume-house site. It does not use the XtraPOS API. From that directory, run `npm install` and `npm run dev`, then open http://localhost:5174. See `dr-salim/README.md`.

## Not in this step

Items, prices, sales, payments, returns, inventory, suppliers, users, authentication, a central server, synchronization, offline sales, SQLite, customer display operation, printers, cash drawer, promotions, legacy import, and a SQL Server installer.
