# INTG_FIRE_PANEL_AVENAR

Integración bidireccional entre el panel de incendio **Bosch Avenar** (protocolo OPC DA Classic) y el sistema de gestión de video **ISS SecurOS** (protocolo IIDK).

---

## Arquitectura

El proyecto implementa **Arquitectura Hexagonal (Ports & Adapters)** organizada en tres capas numeradas. La regla fundamental es que las dependencias solo apuntan hacia adentro: L3 → L1, L2 → L1. Nunca hacia afuera.

```
╔════════════════════════════════════════════════════════════════╗
║                        ENTRY POINT                             ║
║                                                                ║
║   Program.cs  ──►  App.cs  (Composition Root)                  ║
║                       │                                        ║
║                       │  instancia adaptadores y casos de uso  ║
╚════════════════════════════════════════════════════════════════╝
                        │
╔════════════════════════════════════════════════════════════════╗
║                   L3 · INFRASTRUCTURE                          ║
║                                                                ║
║   ┌──────────────────────┐   ┌──────────────────────────────┐  ║
║   │  OpcAdapter          │   │  IidkAdapter                 │  ║
║   │  └─ OpcDaClient      │   │  └─ SecurOsConnector         │  ║
║   │     └─ OpcNetApi DLL │   │     └─ SecurosIIDK SDK       │  ║
║   └──────────┬───────────┘   └──────────────┬───────────────┘  ║
║              │                              │                  ║
║   OpcIntegrationExporter        SecurOsMessage (DTO interno)   ║
╚══════════════╪══════════════════════════════╪══════════════════╝
               │ IOpcGateway                  │ ISecurOsGateway
╔══════════════╪══════════════════════════════╪══════════════════╗
║              │        L2 · APPLICATION      │                  ║
║              │                              │                  ║
║   ┌──────────▼──────────┐   ┌──────────────▼───────────────┐   ║
║   │ SyncItemStateUseCase│   │ ExecuteSecurOsCommandUseCase │   ║
║   │                     │   │                              │   ║
║   │ OPC → SecurOS       │   │ SecurOS → OPC                │   ║
║   └─────────────────────┘   └──────────────────────────────┘   ║
╚════════════════════════════════════════════════════════════════╝
               │ usa                          │ usa
╔══════════════╪══════════════════════════════╪══════════════════╗
║              │         L1 · DOMAIN          │                  ║
║              │                              │                  ║
║   ┌──────────▼──────────┐   ┌──────────────▼──────────────┐    ║
║   │   IOpcGateway       │   │   ISecurOsGateway           │    ║
║   │   (puerto OPC)      │   │   (puerto IIDK)             │    ║
║   └─────────────────────┘   └─────────────────────────────┘    ║
║                                                                ║
║   ┌─────────────────────┐   ┌─────────────────────────────┐    ║
║   │  ItemStateChanged   │   │  SecurOsCommand             │    ║
║   │  (cambio OPC)       │   │  (comando SecurOS)          │    ║
║   └─────────────────────┘   └─────────────────────────────┘    ║
╚════════════════════════════════════════════════════════════════╝
```

---

## Flujo de datos

### OPC → SecurOS (monitoreo de panel)

```
Panel Avenar
    │  cambio de estado (entero)
    ▼
OpcDaClient.PanelChanged
    │  ItemValueResult[]
    ▼
OpcAdapter.MapAndRaise
    │  ItemStateChanged[]  (tipo de dominio)
    ▼
SyncItemStateUseCase.Execute
    │  nodeId + rawValue
    ▼
IidkAdapter.SendItemState
    │  "ObjType|Id|stateName"
    ▼
SecurOsConnector → SecurOS IIDK
```

### SecurOS → OPC (ejecución de comandos)

```
SecurOS
    │  mensaje IIDK (ObjType, Id, Action)
    ▼
SecurOsConnector.MessageReceived
    │  SecurOsMessage (DTO interno)
    ▼
IidkAdapter.MapAndRaise
    │  SecurOsCommand  (tipo de dominio)
    ▼
ExecuteSecurOsCommandUseCase.Execute
    │  XML de comando enriquecido
    ▼
OpcAdapter.WriteCommand
    │  escribe en ítem "CMD"
    ▼
OpcDaClient → Panel Avenar
```

---

## Diagrama de integración

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        Bosch Avenar Fire Panel                               │
│                   hardware  +  OPC DA Server (driver instalado en el PC)     │
└────────────────────────────────────┬─────────────────────────────────────────┘
                                     │
                         protocolo   OPC DA Classic
                         URL leída de OPCTree.json → campo "server"
                         ej: opcda://localhost/Bosch.FPA5000OpcServer.1
                         polling cada  monitoring_update_rate_ms = 500 ms
                                     │
                    ┌────────────────┴─────────────────┐
                    │  cambios de estado (int OPC)     │  comandos XML → ítem CMD
                    ▼                                  ▲
┌──────────────────────────────────────────────────────────────────────────────┐
│                        INTG_FIRE_PANEL_AVENAR.exe                            │
│                                                                              │
│   OPC → SecurOS                                                              │
│   valor entero OPC  ──►  opc_states.json  ──►  nombre de estado  ──►  IIDK   │
│                                                                              │
│   SecurOS → OPC                                                              │
│   comando IIDK  ──►  OPCTree.json (commands)  ──►  XML enriquecido  ──►  CMD │
└────────────────────────────────────┬─────────────────────────────────────────┘
                                     │
                         protocolo   IIDK TCP/IP
                         securos_host = 127.0.0.1
                         securos_port = 3000
                         idUinP       = 3
                                     │
                    ┌────────────────┴─────────────────┐
                    │  estados → eventos SecurOS        │  comandos ← operador
                    ▼                                   ▲
┌──────────────────────────────────────────────────────────────────────────────┐
│                              ISS SecurOS                                     │
│                                                                              │
│   Tipos de objeto generados con  ddi_prefix = AVEJ1_                         │
│                                                                              │
│   ┌──────────────────────┐   ┌───────────────────┐   ┌───────────────────┐   │
│   │  AVEJ1_FIRE_PANEL    │   │    AVEJ1_LZ       │   │    AVEJ1_GR       │   │
│   │  (panel completo)    │   │    (zonas)        │   │    (grupos)       │   │
│   └──────────────────────┘   └───────────────────┘   └───────────────────┘   │
│                                                                              │
│   securos_service_name  = VmsServer                                          │
│   securos_install_dir   = C:\Program Files (x86)\ISS\SecurOS                 │
│   securos_integration_dir = ...\ISS\SecurOS\integrations                     │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Resumen de valores de configuración por capa

| Capa              | Clave de configuración      | Valor ejemplo                                |
| ----------------- | --------------------------- | -------------------------------------------- |
| OPC DA (conexión) | `OPCTree.json → server`     | `opcda://localhost/Bosch.FPA5000OpcServer.1` |
| OPC DA (polling)  | `monitoring_update_rate_ms` | `500`                                        |
| IIDK (host)       | `securos_host`              | `127.0.0.1`                                  |
| IIDK (puerto)     | `securos_port`              | `3000`                                       |
| IIDK (ID módulo)  | `idUinP`                    | `3`                                          |
| Tipos SecurOS     | `ddi_prefix`                | `AVEJ1_`                                     |
| Filtro de tipos   | `allowed_types`             | _(vacío = todos)_                            |
| Servicio Windows  | `securos_service_name`      | `VmsServer`                                  |
| Ruta DDI/DBI      | `securos_install_dir`       | `C:\Program Files (x86)\ISS\SecurOS`         |
| Ruta JSON         | `securos_integration_dir`   | `...\ISS\SecurOS\integrations`               |

---

## Estructura del proyecto

```
INTG_FIRE_PANEL_AVENAR/
│
├── Program.cs                          Entry point (Main)
├── App.cs                              Composition root + flujo de consola
│
├── L1_Domain/                          Sin dependencias externas
│   ├── IOpcGateway.cs                  Puerto de salida hacia OPC
│   ├── ISecurOsGateway.cs              Puerto de salida hacia IIDK
│   ├── ItemStateChanged.cs             Evento: cambio de estado OPC
│   └── SecurOsCommand.cs               Evento: comando de SecurOS
│
├── L2_Application/                     Solo depende de L1
│   ├── SyncItemStateUseCase.cs         Caso de uso: OPC → SecurOS
│   └── ExecuteSecurOsCommandUseCase.cs Caso de uso: SecurOS → OPC
│
├── L3_Infrastructure/                  Depende de L1 + DLLs externas
│   ├── OpcAdapter.cs                   Implementa IOpcGateway
│   ├── OpcDaClient.cs                  Wraps OpcNetApi DLL (OPC DA Classic)
│   ├── IidkAdapter.cs                  Implementa ISecurOsGateway
│   ├── SecurOsConnector.cs             Wraps SecurosIIDK SDK
│   ├── OpcIntegrationExporter.cs       Exporta árbol OPC a JSON/DDI/DBI
│   └── SecurOsMessage.cs               DTO interno del protocolo IIDK
│
├── Config/
│   ├── App.config                      Configuración del puesto
│   ├── opc_states.json                 Mapeo de valores OPC → nombres de estado
│   ├── tree/                           Árbol exportado del servidor OPC (OPCTree.json)
│   └── integration/                    Archivos DDI/DBI/JSON para SecurOS
│
├── Libs/OPC/                           DLLs OPC DA Classic (OpcNetApi, OpcCom)
└── Properties/
```

---

## Configuración (`App.config`)

El archivo `INTG_FIRE_PANEL_AVENAR\Config\App.config` contiene toda la configuración del puesto. **No requiere recompilar** — se edita con cualquier editor de texto.

### Conexión SecurOS (IIDK)

| Clave          | Ejemplo     | Descripción                                                                                                |
| -------------- | ----------- | ---------------------------------------------------------------------------------------------------------- |
| `securos_host` | `127.0.0.1` | IP del servidor SecurOS. Si está en la misma máquina, dejar `127.0.0.1`                                    |
| `securos_port` | `3000`      | En SecurOS: `Configuración del sistema > IIDK > Puerto`. Por defecto es `3000`                             |
| `idUinP`       | `3`         | En SecurOS: `Administración > Hardware > IIDK` — es el **ID** del objeto IIDK creado para esta integración |

> **Cómo crear el objeto IIDK en SecurOS:**
> Administración → árbol de hardware → clic derecho en el servidor → Agregar → IIDK → asignar un ID numérico libre. Ese número es `idUinP`.

### Archivos de integración (DDI/DBI/JSON)

| Clave             | Ejemplo   | Descripción                                                                                                |
| ----------------- | --------- | ---------------------------------------------------------------------------------------------------------- |
| `ddi_prefix`      | `AVEJ1_`  | Prefijo para nombrar los archivos exportados. Se recomienda incluir el nombre del panel o zona             |
| `allowed_types`   | _(vacío)_ | Filtra los tipos OPC a exportar (ej: `LZ,GR`). Vacío exporta todos                                         |
| `output_base_dir` | `../..`   | Directorio base para los archivos exportados, relativo al `.exe`. `../..` apunta a la raíz del repositorio |

### Rutas de SecurOS

| Clave                     | Ejemplo                                           | Descripción                                                                                                                                   |
| ------------------------- | ------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `securos_install_dir`     | `C:\Program Files (x86)\ISS\SecurOS`              | Ruta de instalación de SecurOS. Ver en `Panel de control > Programas > ISS SecurOS`                                                           |
| `securos_integration_dir` | `C:\Program Files (x86)\ISS\SecurOS\integrations` | Subcarpeta `integrations` dentro del directorio anterior. Allí va el `.json` de integración                                                   |
| `securos_service_name`    | `VmsServer`                                       | Nombre del servicio Windows de SecurOS. Abrir `services.msc`, buscar el servicio ISS y copiar el campo **Nombre** (no el nombre para mostrar) |

### Monitoreo

| Clave                       | Ejemplo | Descripción                                                                       |
| --------------------------- | ------- | --------------------------------------------------------------------------------- |
| `monitoring_update_rate_ms` | `500`   | Frecuencia de suscripción OPC en milisegundos. Valores recomendados: `500`–`1000` |

---

## Requisitos

- Windows (COM requerido por OPC DA Classic)
- .NET Framework 4.8
- Visual Studio 2022+ _(solo para compilar)_
- SecurOS instalado y con servicio activo
- Servidor OPC DA activo (Bosch Avenar con driver OPC instalado)
- Ejecutar como **Administrador** (requerido para copiar DDI/DBI a directorios de SecurOS)

---

## Compilar para Release

Abrir **Developer Command Prompt for VS** (menú inicio de Windows) y ejecutar:

```cmd
cd C:\ISS_WORKSPACE\SSA_INTG_BOSCH_AVENAR\INTG_OPCAvenar_IIDKSecuros_V2.0

msbuild INTG_FIRE_PANEL_AVENAR\INTG_FIRE_PANEL_AVENAR.csproj /p:Configuration=Release /p:Platform="Any CPU"
```

El ejecutable queda en:

```
INTG_FIRE_PANEL_AVENAR\bin\Release\INTG_FIRE_PANEL_AVENAR.exe
```

> También se puede compilar desde Visual Studio con **Ctrl + Shift + B** seleccionando la configuración `Release` en el desplegable superior.

---

## Instalación y arranque

### Paso 1 — Prerrequisitos del sistema

Verificar que estén instalados antes de continuar:

| Componente                    | Cómo verificar                                                  |
| ----------------------------- | --------------------------------------------------------------- |
| .NET Framework 4.8            | `Panel de control > Programas > .NET Framework`                 |
| Driver OPC DA de Bosch Avenar | `OPC Core Components` visibles en `regedit > HKLM\SOFTWARE\OPC` |
| ISS SecurOS                   | Servicio `VmsServer` visible en `services.msc`                  |

---

### Paso 2 — Configurar `App.config`

Editar el archivo `INTG_FIRE_PANEL_AVENAR\Config\App.config` con los valores del puesto. Ver la sección [Configuración](#configuración-appconfig) más arriba.

---

### Paso 3 — Compilar

Ver sección [Compilar para Release](#compilar-para-release).

---

### Paso 4 — Modos de arranque

La aplicación tiene dos modos de ejecución seleccionables por argumento de línea de comandos.

> Abrir **CMD como Administrador** y navegar a la carpeta del `.exe`:
>
> ```cmd
> cd INTG_FIRE_PANEL_AVENAR\bin\Release
> ```

---

#### Modo integración `--integration`

Descubre el servidor OPC, exporta el árbol del panel y despliega los archivos en SecurOS. **Usar la primera vez o cuando cambie la configuración del panel.**

```cmd
INTG_FIRE_PANEL_AVENAR.exe --integration
```

**Flujo:**

```
1. Descubre servidores OPC disponibles y solicita selección
        → Guarda la URL seleccionada en OPCTree.json
2. [APP] ¿Desea sobreescribir los archivos existentes? → s/n
        → Exporta OPCTree.json, commands.json, DDI, DBI y JSON
3. [APP] ¿Desea copiar los archivos de integración a SecurOS? → s/n
        → Copia DDI y DBI a securos_install_dir
        → Copia JSON a securos_integration_dir
4. [!]  ATENCION: El servicio SecurOS se detendra
   [APP] ¿Desea continuar? → s/n
        → Detiene VmsServer
        → Marca el DBI como pendiente de importar
        → Reinicia el servicio
```

> Después del reinicio, SecurOS importa el DDI/DBI automáticamente (~1–2 min). Luego ejecutar en modo monitor.

---

#### Modo monitor _(por defecto)_

Lee la URL del servidor OPC desde `OPCTree.json`, conecta directamente sin descubrimiento e inicia el monitoreo bidireccional en tiempo real. **Uso diario normal.**

```cmd
INTG_FIRE_PANEL_AVENAR.exe
```

**Flujo:**

```
1. Lee OPCTree.json desde Config\tree\
        → Extrae la URL del servidor OPC guardada en --integration
        → Conecta directamente al servidor sin descubrimiento
2. [APP] Verifica que el servicio SecurOS esté activo
   [APP] ¿Desea iniciar el monitoreo? → s/n
        → Conecta con SecurOS vía IIDK
        → Suscribe cambios del panel OPC
        → Integración activa — presionar cualquier tecla para detener
```

> Si `OPCTree.json` no existe o no contiene la URL del servidor, la aplicación muestra el error en rojo, lo registra en `error.log` y espera una tecla antes de cerrar. En ese caso ejecutar primero con `--integration`.

---

### Diagrama de arranque

```
¿Primera vez o cambio en el panel?
     │
     ├─ SÍ ──► --integration: Descubrir OPC → Exportar → Desplegar → Reiniciar SecurOS
     │                               │
     │                    SecurOS importa objetos (~1-2 min)
     │                               │
     └─ NO ──────────────────────────┴──► (sin flag): Monitoreo activo
```

---

## Despliegue en otro PC o servidor

No es necesario recompilar para cada puesto. El `.exe` es el mismo; solo cambia el `.config`.

### Pasos

1. Copiar la carpeta `bin\Release\` completa al PC destino.

2. En el PC destino, abrir `INTG_FIRE_PANEL_AVENAR.exe.config` con el Bloc de notas y ajustar los valores del puesto:

```xml
<add key="securos_host"             value="192.168.1.50" />
<add key="idUinP"                   value="5" />
<add key="ddi_prefix"               value="AVEJ2_" />
<add key="securos_service_name"     value="VmsServer" />
<add key="securos_install_dir"      value="C:\Program Files (x86)\ISS\SecurOS" />
<add key="securos_integration_dir"  value="C:\Program Files (x86)\ISS\SecurOS\integrations" />
```

3. Ejecutar `--integration` en ese PC para que descubra el servidor OPC local y genere su propio `OPCTree.json`.

> **Importante:** cada puesto tiene su propio `OPCTree.json` con la URL del servidor OPC local. Este archivo nunca se comparte entre máquinas.

### Estructura mínima para despliegue

```
INTG_FIRE_PANEL_AVENAR.exe
INTG_FIRE_PANEL_AVENAR.exe.config    ← editar con valores del puesto
Config\
    opc_states.json
    App.config
Libs\OPC\
    OpcComRcw.dll
    OpcNetApi.dll
    OpcNetApi.Com.dll
    OpcRcw.Comn.dll
(demás DLLs de dependencias)
```

---

## Registro de errores

Cuando ocurre un error crítico (archivo no encontrado, campo faltante, fallo de conexión), la aplicación:

- Muestra el mensaje en **rojo** en la consola con timestamp.
- Hace **append** al archivo `error.log` junto al `.exe`.
- Espera que se presione una tecla antes de cerrar.

Ejemplo de entrada en `error.log`:

```
[2026-06-02 14:35:10] [ERROR] Campo 'server' no encontrado en OPCTree.json — el archivo fue generado con una versión anterior. Ejecute primero con --integration para regenerarlo.
```
