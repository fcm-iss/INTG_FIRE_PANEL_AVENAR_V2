# INTG_FIRE_PANEL_AVENAR_V2

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
               │ IOpcGateway                  │ ISecurityGateway
╔══════════════╪══════════════════════════════╪══════════════════╗
║              │        L2 · APPLICATION      │                  ║
║              │                              │                  ║
║   ┌──────────▼──────────┐   ┌──────────────▼───────────────┐   ║
║   │ SyncItemStateUseCase│   │ ExecuteSecurityCommandUseCase│   ║
║   │                     │   │                              │   ║
║   │ OPC → SecurOS       │   │ SecurOS → OPC                │   ║
║   └─────────────────────┘   └──────────────────────────────┘   ║
╚════════════════════════════════════════════════════════════════╝
               │ usa                          │ usa
╔══════════════╪══════════════════════════════╪══════════════════╗
║              │         L1 · DOMAIN          │                  ║
║              │                              │                  ║
║   ┌──────────▼──────────┐   ┌──────────────▼──────────────┐    ║
║   │   IOpcGateway       │   │   ISecurityGateway          │    ║
║   │   (puerto OPC)      │   │   (puerto IIDK)             │    ║
║   └─────────────────────┘   └─────────────────────────────┘    ║
║                                                                ║
║   ┌─────────────────────┐   ┌─────────────────────────────┐    ║
║   │  ItemStateChanged   │   │  SecurityCommand            │    ║
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
    │  SecurityCommand  (tipo de dominio)
    ▼
ExecuteSecurityCommandUseCase.Execute
    │  XML de comando enriquecido
    ▼
OpcAdapter.WriteCommand
    │  escribe en ítem "CMD"
    ▼
OpcDaClient → Panel Avenar
```

---

## Estructura del proyecto

```
INTG_OPC_AVENAR_NET4_5/
│
├── Program.cs                          Entry point (Main)
├── App.cs                              Composition root + flujo de consola
│
├── L1_Domain/                          Sin dependencias externas
│   ├── IOpcGateway.cs                  Puerto de salida hacia OPC
│   ├── ISecurityGateway.cs             Puerto de salida hacia IIDK
│   ├── ItemStateChanged.cs             Evento: cambio de estado OPC
│   └── SecurityCommand.cs              Evento: comando de SecurOS
│
├── L2_Application/                     Solo depende de L1
│   ├── SyncItemStateUseCase.cs         Caso de uso: OPC → SecurOS
│   └── ExecuteSecurityCommandUseCase.cs Caso de uso: SecurOS → OPC
│
├── L3_Infrastructure/                  Depende de L1 + DLLs externas
│   ├── OpcAdapter.cs                   Implementa IOpcGateway
│   ├── OpcDaClient.cs                  Wraps OpcNetApi DLL (OPC DA Classic)
│   ├── IidkAdapter.cs                  Implementa ISecurityGateway
│   ├── SecurOsConnector.cs             Wraps SecurosIIDK SDK
│   ├── OpcIntegrationExporter.cs       Exporta árbol OPC a JSON/DDI/DBI
│   └── SecurOsMessage.cs               DTO interno del protocolo IIDK
│
├── Config/
│   ├── opc_states.json                 Mapeo de valores OPC → nombres de estado
│   ├── tree/                           Árbol exportado del servidor OPC
│   └── integration/                    Archivos DDI/DBI/JSON para SecurOS
│
├── Libs/OPC/                           DLLs OPC DA Classic (OpcNetApi, OpcCom)
└── Properties/
```

---

## Configuración (`App.config`)

| Clave                       | Descripción                                                 |
| --------------------------- | ----------------------------------------------------------- |
| `securos_host`              | IP del servidor SecurOS                                     |
| `securos_port`              | Puerto IIDK (defecto: 3000)                                 |
| `idUinP`                    | ID de conexión IIDK                                         |
| `ddi_prefix`                | Prefijo para archivos DDI/DBI exportados                    |
| `allowed_types`             | Tipos OPC permitidos en la integración (separados por coma) |
| `monitoring_update_rate_ms` | Frecuencia de suscripción OPC en ms (defecto: 1000)         |
| `output_base_dir`           | Directorio base para archivos exportados                    |
| `securos_install_dir`       | Ruta de instalación de SecurOS (para copiar DDI/DBI)        |
| `securos_integration_dir`   | Ruta de integraciones de SecurOS (para copiar JSON)         |
| `securos_service_name`      | Nombre del servicio Windows de SecurOS                      |

---

## Requisitos

- Windows (COM requerido por OPC DA Classic)
- .NET Framework 4.8
- Visual Studio 2022+
- SecurOS instalado y con servicio activo
- Servidor OPC DA activo en localhost (Bosch Avenar con driver OPC)
- Ejecutar como **Administrador** (requerido para copiar DDI/DBI a directorios de SecurOS)

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

El archivo de configuración está en:

```
INTG_OPC_AVENAR_NET4_5\Config\App.config
```

#### Conexión SecurOS (IIDK)

| Clave          | Ejemplo     | Cómo obtener el valor                                                                                      |
| -------------- | ----------- | ---------------------------------------------------------------------------------------------------------- |
| `securos_host` | `127.0.0.1` | IP del servidor SecurOS. Si está en la misma máquina, dejar `127.0.0.1`                                    |
| `securos_port` | `3000`      | En SecurOS: `Configuración del sistema > IIDK > Puerto`. Por defecto es `3000`                             |
| `idUinP`       | `3`         | En SecurOS: `Administración > Hardware > IIDK` — es el **ID** del objeto IIDK creado para esta integración |
| `username`     | `ISS`       | Usuario de SecurOS con permisos de operador                                                                |
| `password`     | `pass`      | Contraseña del usuario de SecurOS                                                                          |

> **Cómo crear el objeto IIDK en SecurOS:**
> Administración → árbol de hardware → clic derecho en el servidor → Agregar → IIDK → asignar un ID numérico libre. Ese número es `idUinP`.

#### Archivos de integración (DDI/DBI/JSON)

| Clave             | Ejemplo   | Descripción                                                                                                |
| ----------------- | --------- | ---------------------------------------------------------------------------------------------------------- |
| `ddi_prefix`      | `AVEJ1_`  | Prefijo libre para nombrar los archivos exportados. Se recomienda incluir el nombre del panel o zona       |
| `allowed_types`   | _(vacío)_ | Filtra los tipos OPC a exportar (ej: `LZ,GR`). Vacío exporta todos                                         |
| `output_base_dir` | `../..`   | Directorio base para los archivos exportados, relativo al `.exe`. `../..` apunta a la raíz del repositorio |

#### Rutas de SecurOS

| Clave                     | Ejemplo                                           | Cómo obtener el valor                                                                                                                                           |
| ------------------------- | ------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `securos_install_dir`     | `C:\Program Files (x86)\ISS\SecurOS`              | Ruta de instalación de SecurOS. Ver en `Panel de control > Programas > ISS SecurOS` o buscar el directorio donde está `SecurOS.exe`                             |
| `securos_integration_dir` | `C:\Program Files (x86)\ISS\SecurOS\integrations` | Subcarpeta `integrations` dentro del directorio anterior. Allí van el `.json` de la integración                                                                 |
| `securos_service_name`    | `VmsServer`                                       | Nombre del servicio Windows de SecurOS. Abrir `services.msc` y buscar el servicio ISS. Copiar el nombre exacto del campo **Nombre** (no el nombre para mostrar) |

#### Monitoreo

| Clave                       | Ejemplo | Descripción                                                                       |
| --------------------------- | ------- | --------------------------------------------------------------------------------- |
| `monitoring_update_rate_ms` | `500`   | Frecuencia de suscripción OPC en milisegundos. Valores recomendados: `500`–`1000` |

---

### Paso 3 — Compilar

Abrir la solución `INTG_FIRE_PANEL_AVENAR_V2.slnx` en Visual Studio 2022 y compilar con **Ctrl + Shift + B** en modo `Debug` o `Release`.

---

### Paso 4 — Modos de arranque

La aplicación tiene dos modos de ejecución seleccionables por argumento de línea de comandos.

> Ejecutar siempre como **Administrador**.

---

#### Modo integración `--integration`

Genera los archivos de configuración del panel y los despliega en SecurOS. **Usar la primera vez o cuando cambie la configuración del panel.**

**Desde terminal:**
```
INTG_OPC_AVENAR_NET4_5.exe --integration
```

**Desde Visual Studio:**
`Propiedades del proyecto > Depurar > Argumentos de la línea de comandos` → escribir `--integration`

**Flujo:**
```
1. Seleccionar servidor OPC (auto-descubierto)
2. [APP] ¿Desea sobreescribir los archivos existentes? → s/n
        → Exporta treeExporter.json, commands.json, DDI, DBI y JSON
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

#### Modo monitor *(por defecto)*

Conecta con SecurOS y el panel OPC e inicia el monitoreo bidireccional en tiempo real. **Uso diario normal.**

**Desde terminal:**
```
INTG_OPC_AVENAR_NET4_5.exe
```

**Desde Visual Studio:**
Ejecutar con **F5** sin ningún argumento configurado.

**Flujo:**
```
1. Seleccionar servidor OPC (auto-descubierto)
        → Carga treeExporter.json desde Config\tree\
2. [APP] ¿Desea iniciar el monitoreo? → s/n
        → Conecta con SecurOS vía IIDK
        → Suscribe cambios del panel OPC
        → Integración activa — presionar cualquier tecla para detener
```

> Si no existe `treeExporter.json`, la aplicación avisa que se debe ejecutar primero con `--integration`.

---

### Diagrama de arranque

```
¿Primera vez o cambio en el panel?
     │
     ├─ SÍ ──► --integration: Exportar → Desplegar → Reiniciar SecurOS
     │                               │
     │                    SecurOS importa objetos (~1-2 min)
     │                               │
     └─ NO ──────────────────────────┴──► (sin flag): Monitoreo activo
```
