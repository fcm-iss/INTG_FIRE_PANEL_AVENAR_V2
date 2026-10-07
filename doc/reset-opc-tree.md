# Relectura del árbol OPC al recibir `INTEGRATION RESET`

En modo monitoreo, cuando SecurOS envía `INTEGRATION RESET` con `Id` igual a `idUinP` (`App.config`),
la aplicación vuelve a leer el árbol del servidor OPC y lo compara con el árbol activo.

> **Estado actual: solo informa.** No crea, renombra ni borra objetos en SecurOS, no reemplaza
> `OPCTree.json` y no añade ítems nuevos a la suscripción OPC.

## Flujo

```
INTEGRATION RESET (Id = idUinP) → SecurOsAdapter.ResetRequested → cola → hilo principal (STA)
  → BuildTree de nuevo                → Config/opc-model/OPCTree.reset.json
  → comparación con OPCTree.json      (DetectOpcTreeChangesUseCase, clave objType|securosId)
  → consola [OPC_Diff] + Config/opc-model/OPCTree.changes.json
```

- La lectura se hace en el hilo principal porque los objetos COM de OPC DA solo pueden usarse desde el hilo
  donde se crearon (la misma cola que ejecuta `WriteCommand`). Mientras dura, los comandos hacia el panel esperan.
- Varios RESET seguidos producen una sola relectura.
- Ambos archivos se sobrescriben en cada RESET.

## `OPCTree.reset.json`

Mismo formato que `OPCTree.json` (lo genera el mismo `BuildTree`): el árbol completo del servidor OPC en el
momento del RESET.

```json
{
  "name": "root",
  "server": "opcda://localhost/Bosch.FPA5000OpcServer/{...}",
  "prefix": "TESTFP_",
  "idUinP": "2",
  "children": [
    {
      "name": "...", "elementType": "branch", "itemName": "...",
      "securosId": "1-2", "objType": "TESTFP_FIRE_PANEL",
      "children": [ ... ]
    }
  ]
}
```

Sirve para inspeccionarlo o compararlo a mano. La aplicación no lo usa para nada más: el monitoreo sigue
trabajando con `OPCTree.json`.

## `OPCTree.changes.json`

Compara solo **los objetos que se crearían en SecurOS** (paneles e ítems con `objType` y `securosId`).
Cada objeto se identifica por `objType|securosId` y su nombre es la propiedad OPC **101** (`DetectorShortText`)
o, si no existe, el `itemName`.

Ejemplo (formateado para leerlo; el archivo real se escribe en una sola línea):

```json
{
  "createdAt": "2026-10-07T12:30:00",
  "added": [
    { "ObjType": "TESTFP_DETECTOR", "ObjId": "1-2.1.7", "Name": "Detector oficina 3", "ParentId": "1-2", "IsPanel": false }
  ],
  "removed": [
    { "ObjType": "TESTFP_NAC", "ObjId": "1-2.2.4", "Name": "Sirena pasillo", "ParentId": "1-2", "IsPanel": false }
  ],
  "renamed": [
    { "ObjType": "TESTFP_DETECTOR", "ObjId": "1-2.1.3", "OldName": "Detector 3", "NewName": "Detector sala reuniones" }
  ]
}
```

| Lista | Significado |
|---|---|
| `added` | Está en el árbol releído y no en `OPCTree.json` |
| `removed` | Está en `OPCTree.json` y ya no en el panel |
| `renamed` | Mismo `ObjType|ObjId`, distinto nombre |

`ParentId` es el id del panel del que cuelga el objeto (vacío en los paneles, que llevan `IsPanel: true`).
Sin cambios, las tres listas vienen vacías y la consola muestra `Sin cambios respecto al árbol activo`.

## Limitaciones conocidas

1. **Se compara siempre contra el mismo `OPCTree.json`.** Como el RESET no lo reemplaza, los cambios se
   acumulan: un elemento nuevo seguirá apareciendo como nuevo en cada RESET hasta volver a ejecutar
   `--initial-setup`.
2. **No se filtra por `allowed_types`.** El informe incluye tipos que no existen en SecurOS
   (ver [allowed-types.md](allowed-types.md)).
3. **Los ids dependen de la posición en el árbol.** Si en el panel se inserta un elemento en medio de un nivel,
   los ids siguientes se desplazan y el informe muestra "eliminados" y "nuevos" que en realidad son los mismos
   objetos. Por eso no se recomienda borrar objetos en SecurOS automáticamente a partir de este informe.
4. Solo se reacciona al RESET cuyo `Id` coincide con `idUinP`; un RESET de otro punto de integración se ignora.
