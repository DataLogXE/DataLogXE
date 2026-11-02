# DataLogXE

A simple and high performance data logger for industrial controllers. DataLogXE reads values and alarms from
Siemens S7-1200 / S7-1500 PLCs and OPC UA servers, stores them in TimescaleDB and shows them in a web UI with
charts, alarm lists and a dashboard. It runs as a set of Docker containers on a PC, on a Raspberry Pi or on a
Siemens SIMATIC IOT2050, with as little as 1 GB of RAM.

## Features

- **Siemens S7CommPlus:** S7-1500 from firmware 2.9 and S7-1200 from firmware 4.3 with secure PG/HMI
  communication (TIA Portal V17 or later), symbolic browse of all data blocks and the I/Q/M areas including the
  multilingual comments, values by subscription, PLC alarms in the configured language. Read only, DataLogXE never
  writes to the PLC.
- **OPC UA:** browse the address space or import a NodeSet XML, subscriptions with a cycle per tag, OPC UA alarms
  and conditions, sign-in with user and password, message security with an explicit trust of the server
  certificate.
- **System:** CPU, memory and disk of the host through Telegraf (with Docker Desktop: of its VM). Telegraf mounts
  the host file system read-only for this.
- **Archive:** raw values plus pre-aggregated levels (min/max/first/last per bucket) in compressed Timescale
  hypertables, so a chart over months loads as fast as one over minutes. Retention is set per level in the UI.
- **Web UI:** charts with zoom, pan, cursors and statistics, export as CSV or Excel; pending and historical alarms
  with alarm statistics; event log per data source; tag lists with tens of thousands of rows; data source and tag
  configuration.
- **Security:** HTTPS only (Caddy with its own CA), users with the roles admin and viewer, stored data source
  passwords encrypted with AES.

## Quick start

Requirements: Docker with Compose on a 64-bit host (amd64 or arm64).

```bash
git clone https://github.com/DataLogXE/datalogxe.git
cd datalogxe
cp .env.example .env
```

Set `POSTGRES_PASSWORD` in `.env`, then start the stack:

```bash
docker compose up -d
```

Open `https://<host>:8443` and create the administrator.

## Operation

### Certificate

The browser warns about the certificate, because it is issued by the internal CA of the proxy. To get rid of the
warning, import the root certificate of that CA into the trusted root certificates of your system or browser:

```bash
docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt datalogxe-root.crt
```

### Update

Back up the database and the `app-data` volume, then in the folder of `compose.yaml`:

```bash
git pull
docker compose up -d
```

The image version is pinned in `compose.yaml`. The app upgrades the database schema on its first start.

### Configuration

Installation settings in `.env` (see [.env.example](.env.example)); everything else is set in the web UI.

| Variable | Default | Meaning |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | – | Database password, required, without `;`. Only taken on the first start. |
| `HTTPS_PORT` | `8443` | Port of the web UI. |
| `PG_SHARED_BUFFERS` | `128MB` | Database memory; 256MB suits a PC with 4 GB or more. |
| `DATALOG_MASTER_KEY` | `master.key` in `app-data` | Key for the stored data source passwords (base64, 32 bytes). |

The data lives in the Docker volumes `timescale-data`, `app-data` (master key, OPC UA certificates) and
`caddy-data` (internal CA). Back up `app-data` together with the database, otherwise the stored data source
passwords have to be entered again.

## Architecture

| Container | Image | Role |
| --- | --- | --- |
| `datalogxe` | `datalogxe/datalogxe` | API, data sources and the Vue UI in one .NET process |
| `proxy` | `caddy` | HTTPS in front of the API, the only published port; fetches its Caddyfile from the app |
| `timescaledb` | `timescale/timescaledb` | PostgreSQL with TimescaleDB, configuration and archive |
| `telegraf` | `telegraf` | Host metrics for the System data source |

Source layout:

| Folder | Content |
| --- | --- |
| `DataLogApi` | ASP.NET Core host: REST API, sign-in, serves the UI |
| `DataLogService` | Data sources (OPC UA, S7CommPlus, System, Test) and the logging service |
| `DataLogLib` | Data access, archive, schema upgrades, crypto, OPC UA NodeSet import |
| `S7CommPlus` | S7CommPlus protocol driver |
| `DataLogLib.Tests` | xUnit tests |
| `datalog-ui` | Vue 3 + TypeScript + Vite web UI |

## License

Copyright (C) 2026 Robert Mederer - [DataLogXE](https://github.com/DataLogXE). Licensed under GPL-3.0-or-later.
See [LICENSE](LICENSE). The licenses of the third-party components are listed in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

The S7CommPlus driver in [S7CommPlus](S7CommPlus) is licensed under the
[GNU Lesser General Public License v3.0 or later](S7CommPlus/LICENSE). It contains files of
[S7CommPlusDriver](https://github.com/thomas-v2/S7CommPlusDriver) by Thomas Wiens and its contributors, some of
them modified by DataLogXE, and files of DataLogXE; the header of each file names its origin, its authors and
whether DataLogXE changed it. Parts are based on Snap7 (Sharp7) by Davide Nardella (LGPL); the symbol CRC algorithm
was informed by [HarpoS7](https://github.com/bonk-dev/HarpoS7) (MIT).

Siemens, SIMATIC, S7-1200, S7-1500, IOT2050 and TIA Portal are trademarks of Siemens AG. Raspberry Pi is a
trademark of Raspberry Pi Ltd. DataLogXE is not affiliated with or endorsed by Siemens or Raspberry Pi Ltd.
