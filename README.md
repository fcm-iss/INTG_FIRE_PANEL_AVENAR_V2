# INTG_FIRE_PANEL_AVENAR

Integración bidireccional entre el panel de incendio **Bosch Avenar** (protocolo OPC DA Classic) y el sistema de gestión de video **ISS SecurOS** (protocolo UnIP).

---
---

## Validado con

| Componente                | Versión         |
| ------------------------- | --------------- |
| OS                        | Windows 11 Home |
| Bosch FSM-5000 OPC Server | 2.0.25          |
| Firmware panel Avenar     | 4.5.2           |
| ISS SecurOS               | 12.1            |
| .NET Framework            | 4.8             |

---
---

## Requisitos

- Windows (COM requerido por OPC DA Classic)
- .NET Framework 4.8
- Visual Studio 2022+ _(solo para compilar)_
- SecurOS instalado y con servicio activo
- Servidor OPC DA activo (Bosch Avenar con driver OPC instalado)
- Ejecutar como **Administrador** (requerido para copiar DDI/DBI a directorios de SecurOS)

---
---

## Instalación y arranque

### Paso 1 — Prerrequisitos del sistema

Verificar que estén instalados antes de continuar:

| Componente                    | Cómo verificar                                                  |
| ----------------------------- | --------------------------------------------------------------- |
| .NET Framework 4.8            | `Panel de control > Programas > .NET Framework`                 |
| Driver OPC DA de Bosch Avenar | `OPC Core Components` visibles en `regedit > HKLM\SOFTWARE\OPC` |
| ISS SecurOS                   | Servicio `VmsServer` visible en `services.msc`                  |

### Paso 2 — Crear punto de integración en SecurOS

Antes de configurar el `App.config` se necesita el ID del objeto de integración creado en SecurOS.

1. **Árbol de configuración** — En el servidor de configuración, expande el nodo **Integraciones y Automatizaciones**.
2. **Crear punto de integración** — Clic derecho sobre ese nodo → **Create object** → selecciona **Universal Integration Point**.
3. **Configurar el objeto** — En el panel de propiedades:
   - **Name:** nombre descriptivo (ej. `UnIP_INTG_AVENAR`)
   - **ID:** número único — anótalo, lo necesitarás en el siguiente paso (ej: [1])
   - **Puerto:** `3000` (por defecto)
   - Guarda con **Apply / OK**
4. **Copiar el ID** — El ID asignado al objeto es el valor que va en `idUinP` del `App.config`.

### Paso 3 — Configurar `App.config`

Editar el archivo `INTG_FIRE_PANEL_AVENAR\Config\App.config` con los valores del puesto. Ver la sección [Configuración](#configuración-appconfig) más abajo.

### Paso 4 — Compilar

Ver sección [Compilar para Release](#compilar-para-release).

### Paso 5 — Crear la Aplicación Externa (EXT-APP) en SecurOS

Una vez compilado el `.exe`, registrarlo en SecurOS como **Aplicación Externa** para que el sistema pueda lanzarlo y supervisarlo.

En **Integraciones y Automatizaciones**, clic derecho → **Create object** → **External Application**. Configurar los siguientes campos:

| Campo                  | Valor                                                                 |
| ---------------------- | --------------------------------------------------------------------- |
| **Name**               | `EXT-APP_INTG_AVENAR`                                                 |
| **Aplicación**         | Ruta completa al `.exe` (ej: `C:\apps\INTG_FIRE_PANEL_AVENAR\INTG_FIRE_PANEL_AVENAR.exe`) |
| **Argumento**          | Dejar vacío _(el modo de arranque se controla manualmente por consola)_ |
| **Directorio de trabajo** | Carpeta `bin\Release\` donde están el `.exe` y todas sus `.dll`    |

> En .NET Framework el ejecutable es directamente el `.exe` generado por la compilación. No se necesita intérprete externo (a diferencia de Node.js, Python, etc.).

**Archivos que deben estar en el directorio de trabajo:**

```
INTG_FIRE_PANEL_AVENAR.exe
INTG_FIRE_PANEL_AVENAR.exe.config     ← configuración del puesto
Config\
    opc_states.json
    App.config
Libs\OPC\
    OpcComRcw.dll
    OpcNetApi.dll
    OpcNetApi.Com.dll
    OpcRcw.Comn.dll
(demás .dll de dependencias)
```

**Ubicación del objeto en el árbol ISS:**

```
Servidores y Estaciones de Trabajo
└── [Servidor] Computadora ISS
    └── Integraciones y Automatizaciones
        ├── UnIP_INTG_AVENAR          ← punto de integración (Paso 2)
        └── EXT-APP_INTG_AVENAR       ← aplicación externa (este paso)
```

**Referencia rápida .NET Framework:**

| Aspecto             | Valor                              |
| ------------------- | ---------------------------------- |
| Ejecutable          | `INTG_FIRE_PANEL_AVENAR.exe`       |
| Argumento           | _(generalmente vacío)_             |
| Dependencias        | `.dll` incluidas en `bin\Release\` |
| Configuración       | `App.config` / `.exe.config`       |
| Compilación previa  | Requerida (modo Release)           |

### Paso 6 — Modos de arranque

La aplicación tiene dos modos de ejecución seleccionables por argumento de línea de comandos.

> Abrir **CMD como Administrador** y navegar a la carpeta del `.exe`:
>
> ```cmd
> cd INTG_FIRE_PANEL_AVENAR\bin\Release
> ```

<blockquote>

#### Modo integración `--integration`

> **Cuándo ejecutar este modo:**
> Solo en dos situaciones: la **primera puesta en marcha** del puesto, o cuando se realice una **actualización en la configuración del panel** (se agregaron o eliminaron zonas, grupos u otros objetos). En operación normal diaria este modo **no se vuelve a ejecutar**.

> [!WARNING]
> **Requiere PowerShell como Administrador.**
> Este modo copia archivos a directorios protegidos de SecurOS y detiene/reinicia el servicio `VmsServer`. Sin permisos de administrador el proceso fallará silenciosamente o lanzará un error de acceso.
>
> Abrir PowerShell: clic derecho en el icono → **Ejecutar como administrador**.

Este modo conecta al servidor OPC, descubre todos los objetos que el panel tiene configurados, genera los archivos de definición (DDI/DBI/JSON) y los despliega en SecurOS. Al finalizar, SecurOS importa automáticamente los tipos y crea los objetos en el punto de integración (UnIP).

```powershell
# PowerShell como Administrador
cd INTG_FIRE_PANEL_AVENAR\bin\Release
.\INTG_FIRE_PANEL_AVENAR.exe --integration
```

**Diagrama de flujo:**

```
 INTG_FIRE_PANEL_AVENAR.exe --integration
              │
              ▼
 ┌────────────────────────────────────────┐
 │  Descubre servidores OPC disponibles   │
 │  Muestra lista → usuario selecciona    │
 │  Guarda URL en OPCTree.json            │
 └────────────────────┬───────────────────┘
                      │
                      ▼
 [APP] ¿Sobreescribir archivos existentes? → n ──► Termina
                      │ s
                      ▼
 ┌────────────────────────────────────────┐
 │  Exporta árbol del panel               │
 │    OPCTree.json   (árbol completo)     │
 │    commands.json  (comandos OPC)       │
 │    <prefix>.ddi   (tipos de objeto)    │
 │    <prefix>.dbi   (objetos del panel)  │
 │    <prefix>.json  (integración UnIP)   │
 └────────────────────┬───────────────────┘
                      │
                      ▼
 [APP] ¿Copiar archivos a SecurOS? → n ──────────► Termina
                      │ s
                      ▼
 ┌────────────────────────────────────────┐
 │  Copia DDI + DBI → securos_install_dir │
 │  Copia JSON      → securos_integration_dir │
 └────────────────────┬───────────────────┘
                      │
                      ▼
 [!] ATENCIÓN: El servicio SecurOS se detendrá
 [APP] ¿Desea continuar? → n ────────────────────► Termina
                      │ s
                      ▼
 ┌────────────────────────────────────────┐
 │  Detiene el servicio VmsServer         │
 │  Marca el DBI como pendiente           │
 │  Reinicia el servicio VmsServer        │
 └────────────────────┬───────────────────┘
                      │
                      ▼
 SecurOS importa DDI/DBI automáticamente
 (~1–2 min después del reinicio)
              │
              ▼
 ✓ Objetos del panel disponibles en el UnIP
   → Ejecutar ahora en modo monitor
```

</blockquote>

<blockquote>

#### Modo monitor _(por defecto)_

> **Cuándo ejecutar este modo:**
> Es el **modo de operación normal**. Se usa una vez que el modo `--integration` ya fue ejecutado y SecurOS tiene todos los objetos del panel creados en el punto de integración (UnIP). En este estado la aplicación conecta directamente y mantiene la integración activa en tiempo real.

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
        → Conecta con SecurOS vía UnIP
        → Suscribe cambios del panel OPC
        → Integración activa — presionar cualquier tecla para detener
```

> Si `OPCTree.json` no existe o no contiene la URL del servidor, la aplicación muestra el error en rojo, lo registra en `error.log` y espera una tecla antes de cerrar. En ese caso ejecutar primero con `--integration`.

</blockquote>

### Diagrama de decisión: ¿qué modo usar?

```
 ¿Es la primera vez que se pone en marcha este puesto?
 ¿O se modificó la configuración del panel (zonas, grupos, etc.)?
              │
              ├─ SÍ ──► Ejecutar --integration
              │              │
              │         SecurOS importa objetos (~1–2 min)
              │              │
              │         Verificar que los objetos del panel
              │         aparezcan en el UnIP de SecurOS
              │              │
              └─ NO ─────────┴──► Ejecutar sin flag (modo monitor)
```

---
---

## Configuración (`App.config`)

El archivo `INTG_FIRE_PANEL_AVENAR\Config\App.config` contiene toda la configuración del puesto. **No requiere recompilar** — se edita con cualquier editor de texto.

### Conexión SecurOS (UnIP)

| Clave          | Ejemplo     | Descripción                                                                                 |
| -------------- | ----------- | ------------------------------------------------------------------------------------------- |
| `securos_host` | `127.0.0.1` | IP del servidor SecurOS. Si está en la misma máquina, dejar `127.0.0.1`                     |
| `securos_port` | `3000`      | En SecurOS: Por defecto es `3000`                                                           |
| `idUinP`       | `1`         | En SecurOS: Es el **ID** del objeto UnIP (Puno de integración) creado para esta integración |

> Ver [Paso 2 — Crear punto de integración en SecurOS](#paso-2--crear-punto-de-integración-en-securos) para el procedimiento completo.

### Archivos de integración (DDI/DBI/JSON)

| Clave             | Ejemplo        | Descripción                                                                                                |
| ----------------- | -------------- | ---------------------------------------------------------------------------------------------------------- |
| `ddi_prefix`      | `INTG_AVENAR_` | Prefijo para nombrar los archivos exportados. Se recomienda incluir el nombre del panel o zona             |
| `allowed_types`   | _(vacío)_      | Filtra los tipos OPC a exportar (ej: `NAC, DETECTOR`). Vacío exporta todos                                 |
| `output_base_dir` | `../..`        | Directorio base para los archivos exportados, relativo al `.exe`. `../..` apunta a la raíz del repositorio |

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
---

## Despliegue en otro PC o servidor

No es necesario recompilar para cada puesto. El `.exe` es el mismo; solo cambia el `.config`.

### Pasos

1. Copiar la carpeta `bin\Release\` completa al PC destino.

2. En el PC destino, abrir `INTG_FIRE_PANEL_AVENAR.exe.config` con el Bloc de notas y ajustar los valores del puesto:

```xml
<add key="securos_host"             value="192.168.1.50" />
<add key="idUinP"                   value="1" />
<add key="ddi_prefix"               value="INTG_AVENAR_" />
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
---

## Compilar para Release

Abrir **Developer Command Prompt for VS** (menú inicio de Windows) y ejecutar:

```cmd
cd C:\ISS_WORKSPACE\SSA_INTG_BOSCH_AVENAR\INTG_OPCAvenar_<<<UnIPSECUROS>>>_V2.0

msbuild INTG_FIRE_PANEL_AVENAR\INTG_FIRE_PANEL_AVENAR.csproj /p:Configuration=Release /p:Platform="Any CPU"
```

El ejecutable queda en:

```
INTG_FIRE_PANEL_AVENAR\bin\Release\INTG_FIRE_PANEL_AVENAR.exe
```

> También se puede compilar desde Visual Studio con **Ctrl + Shift + B** seleccionando la configuración `Release` en el desplegable superior.

---
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
║   │  OpcAdapter          │   │  UnIPAdapter                 │  ║
║   │  └─ OpcDaClient      │   │  └─ SecurOsConnector         │  ║
║   │     └─ OpcNetApi DLL │   │     └─ <<<SECUROSUnIP>>> SDK       │  ║
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
║   │   (puerto OPC)      │   │   (puerto UnIP)             │    ║
║   └─────────────────────┘   └─────────────────────────────┘    ║
║                                                                ║
║   ┌─────────────────────┐   ┌─────────────────────────────┐    ║
║   │  ItemStateChanged   │   │  SecurOsCommand             │    ║
║   │  (cambio OPC)       │   │  (comando SecurOS)          │    ║
║   └─────────────────────┘   └─────────────────────────────┘    ║
╚════════════════════════════════════════════════════════════════╝
```

---
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
UnIPAdapter.SendItemState
    │  "ObjType|Id|stateName"
    ▼
SecurOsConnector → SecurOS UnIP
```

### SecurOS → OPC (ejecución de comandos)

```
SecurOS
    │  mensaje UnIP (ObjType, Id, Action)
    ▼
SecurOsConnector.MessageReceived
    │  SecurOsMessage (DTO interno)
    ▼
UnIPAdapter.MapAndRaise
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
│   valor entero OPC  ──►  opc_states.json  ──►  nombre de estado  ──►  UnIP   │
│                                                                              │
│   SecurOS → OPC                                                              │
│   comando UnIP  ──►  OPCTree.json (commands)  ──►  XML enriquecido  ──►  CMD │
└────────────────────────────────────┬─────────────────────────────────────────┘
                                     │
                         protocolo   UnIP TCP/IP
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
| UnIP (host)       | `securos_host`              | `127.0.0.1`                                  |
| UnIP (puerto)     | `securos_port`              | `3000`                                       |
| UnIP (ID módulo)  | `idUinP`                    | `3`                                          |
| Tipos SecurOS     | `ddi_prefix`                | `AVEJ1_`                                     |
| Filtro de tipos   | `allowed_types`             | _(vacío = todos)_                            |
| Servicio Windows  | `securos_service_name`      | `VmsServer`                                  |
| Ruta DDI/DBI      | `securos_install_dir`       | `C:\Program Files (x86)\ISS\SecurOS`         |
| Ruta JSON         | `securos_integration_dir`   | `...\ISS\SecurOS\integrations`               |

---
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
│   ├── ISecurOsGateway.cs              Puerto de salida hacia UnIP
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
│   ├── UnIPAdapter.cs                  Implementa ISecurOsGateway
│   ├── SecurOsConnector.cs             Wraps <<<SECUROSUnIP>>> SDK
│   ├── OpcIntegrationExporter.cs       Exporta árbol OPC a JSON/DDI/DBI
│   └── SecurOsMessage.cs               DTO interno del protocolo UnIP
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
