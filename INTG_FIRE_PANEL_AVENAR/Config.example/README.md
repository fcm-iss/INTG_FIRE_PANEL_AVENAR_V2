# Config.example

Ejemplos de los archivos que la aplicación genera en `Config/` al ejecutarse con `--initial-setup`.
Son de referencia: la app **no** los lee. Los archivos reales de `Config/opc-model/` y `Config/securos-integration-files/`
están en `.gitignore` porque dependen de cada panel/instalación.

Generados con `ddi_prefix=TESTFP_`, `idUinP=2` y `allowed_types=NAC,Detector`.

| Archivo | Lo genera | Uso |
|---|---|---|
| `opc-model/OPCTree.json` | `ExportOpcTreeWithPropertiesToJson` | Árbol OPC completo con propiedades, `securosId` y `objType`. Lo usa el modo monitoreo y la creación de objetos. |
| `opc-model/commands.json` | `ExportCommandsToJson` | Comandos disponibles en el nodo `CMD` del servidor OPC. |
| `securos-integration-files/<prefijo>.integration.json` | `ExportIntegrationJson` | Estructura de tipos de la integración; se copia a `securos_integration_dir`. |
| `securos-integration-files/securos.<prefijo>.ddi` | `ExportDdiFile` | Definición de tipos de objeto para SecurOS; se copia a `securos_install_dir`. |
| `securos-integration-files/securos.<prefijo>.dbi` | `ExportDbiFile` | Campos de base de datos de cada tipo (según `Config/dbi_fields.json`); se copia a `securos_install_dir`. |

Al recibir `INTEGRATION RESET` en modo monitoreo también se generan en `Config/opc-model/`
`OPCTree.reset.json` (árbol releído) y `OPCTree.changes.json` (objetos nuevos, eliminados y renombrados).
