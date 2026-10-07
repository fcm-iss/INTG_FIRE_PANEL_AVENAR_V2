# Filtro `allowed_types`

Clave de `Config/App.config` que limita qué tipos del panel se integran en SecurOS:

```xml
<add key="allowed_types" value="NAC,Detector" />
```

Vacío = se procesan todos los tipos.

## Alcance actual del filtro

`OPCTree.json` **siempre se genera con el árbol completo** del servidor OPC, sin importar `allowed_types`.
El filtro solo se aplica a los archivos que se despliegan en SecurOS.

| Archivo / flujo | ¿Aplica `allowed_types`? |
|---|---|
| `Config/opc-model/OPCTree.json` | ❌ Árbol completo, todos los tipos |
| `Config/opc-model/commands.json` | ❌ No aplica (solo el nodo `CMD`) |
| `Config/securos-integration-files/*.integration.json` | ✅ |
| `Config/securos-integration-files/*.ddi` | ✅ |
| `Config/securos-integration-files/*.dbi` | ✅ |
| `--create-securos-objects` / creación de objetos tras `--initial-setup` | ❌ Lee `OPCTree.json` completo |
| Monitoreo (suscripción OPC y envío de estados) | ❌ Lee `OPCTree.json` completo |
| Comparación del RESET (`OPCTree.changes.json`) | ❌ Lee el árbol completo |

En `App.cs` (`ExportOpcFiles`), `allowedTypes` solo se pasa a `ExportIntegrationJson`, `ExportDdiFile` y
`ExportDbiFile`. `ExportOpcTreeWithPropertiesToJson` escribe el árbol tal cual lo devuelve el servidor.

## Consecuencias

Con `allowed_types=Detector`, SecurOS solo conoce el tipo `<prefijo>_DETECTOR`, pero el resto de la app
trabaja con todos los tipos del panel:

- **Creación de objetos:** también se envía `CREATE_OBJECT` para NAC, Bypass Group, CAN Bus, etc.
  SecurOS no conoce esos tipos, así que esos envíos deberían fallar (revisar el `rc` que se registra en consola).
- **Monitoreo:** se suscriben todos los ítems del panel y los cambios de estado de cualquier tipo se envían
  a SecurOS (`objType|id|estado`). SecurOS no tiene esos objetos: es tráfico inútil, notable en paneles grandes.
- **RESET:** el informe de cambios incluye tipos que no existen en SecurOS.

## Cómo se compara

`SecurOsFileExporter.IsTypeAllowed` compara cada valor de `allowed_types` con el campo `name` del nodo de tipo
(el nivel inmediatamente debajo del panel). La comparación es **exacta y distingue mayúsculas/minúsculas**:

| Valor en `allowed_types` | Nombre en el servidor OPC | ¿Coincide? |
|---|---|---|
| `Detector` | `Detector` | ✅ |
| `detector` | `Detector` | ❌ |
| `DETECTOR` | `Detector` | ❌ |

Usar los nombres exactamente como aparecen en el campo `name` de `OPCTree.json`.

## Propuesta (pendiente)

- Aplicar `allowed_types` en un solo lugar, al leer el árbol (`OpcTreeReader`), para que la creación de objetos,
  el monitoreo y el RESET trabajen solo con los tipos permitidos, igual que el `.ddi`.
- Hacer la comparación sin distinguir mayúsculas/minúsculas.
- Mantener `OPCTree.json` completo: sirve de referencia de todo lo que tiene el panel y permite añadir un tipo
  a `allowed_types` sin volver a recorrer el servidor OPC.
