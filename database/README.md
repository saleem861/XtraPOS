Runtime database files do not belong in this folder.

XtraPOS keeps branch data in SQL Server, in a database named `XtraPOS_Branch`, collation `Arabic_CI_AS`. The API creates that database and applies the schema migrations under `src/XtraPOS.Api/Data/Migrations`. SQL Server stores the MDF and LDF files in its own data directory, outside this OneDrive project.

Do not create SQLite files, logs, backups, or temporary runtime data here. Do not point the API at `saleemPOSDB`, `Shop1`, `Shop2`, or `swstore`.
