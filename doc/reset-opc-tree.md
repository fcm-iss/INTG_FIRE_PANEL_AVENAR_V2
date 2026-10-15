# Qué hace la integración al recibir `INTEGRATION RESET`

En modo monitoreo, cuando SecurOS envía un `INTEGRATION RESET` cuyo `Id` es igual a `idUinP` (`App.config`),
la aplicación hace tres cosas:

1. Vuelve a leer el árbol del servidor OPC.
2. Pone SecurOS y el monitoreo al día con lo que cambió.
3. Cuando termina, **responde el RESET** a SecurOS.

Un RESET de otro punto de integración (otro `Id`) se ignora.

## Flujo

```
INTEGRATION RESET (Id = idUinP)
  → SecurOsAdapter.ResetRequested → cola OPC → hilo principal (STA) → OpcTreeResetFlow
     1. Relectura del árbol OPC          → Config/opc-model/OPCTree.reset.json
     2. Comparación con OPCTree.json     → Config/opc-model/OPCTree.changes.json
        ── sin cambios: ir al paso 6 ──
     3. Cambios en SecurOS               DELETE_OBJECT → CREATE_OBJECT → UPDATE_OBJECT
     4. Promoción del árbol              OPCTree.json → OPCTree.previous.json
                                         OPCTree.reset.json → OPCTree.json
     5. Recarga del monitoreo            suscripción OPC + mapa de comandos
     6. Respuesta del RESET a SecurOS    <tipo raíz>|<Id>|RESET|{"version":1,"objects":[...]}
```

- Todo se ejecuta en el hilo principal: los objetos COM de OPC DA solo pueden usarse desde el hilo donde se
  crearon. Es la misma cola que ejecuta `WriteCommand`, así que mientras dura el RESET los comandos hacia el
  panel esperan su turno.
- Si llegan varios RESET seguidos, se hace una sola relectura.

Código: `Flows/OpcTreeResetFlow.cs`, `L2_Application/DetectOpcTreeChangesUseCase.cs`,
`L2_Application/ApplyOpcTreeChangesUseCase.cs` y `L3_Infrastructure/SecurOsAdapter.cs` (`SendResetResponse`).

## 1. Relectura del árbol

Se vuelve a ejecutar el mismo `BuildTree` de `--initial-setup`. El resultado se guarda en
`OPCTree.reset.json`, con el mismo formato que `OPCTree.json`.

### Ids estables por grupo

El id de cada objeto en SecurOS tiene la forma `<panel>.<grupo>.<instancia>`, por ejemplo `1-1.8.10`.
El número de **grupo** sale de la tabla `Config/opc_group_ids.json` y no de la posición del grupo en el
servidor OPC:

```json
{ "Aux Power": 1, "Battery": 2, "Mains Power": 3, ..., "Detector": 8, ..., "Monitoring": 28,
  "Error": 29, "Panel": 30, "Rail Power": 31 }
```

- Si aparece o desaparece un grupo, los demás conservan su número. Un mismo dispositivo mantiene su id entre
  lecturas, así que la comparación solo muestra cambios reales.
- Un grupo que no está en la tabla recibe el siguiente número libre. Se avisa por consola y se guarda en la
  copia desplegada del archivo, para que conserve ese número en las lecturas siguientes.

## 2. Comparación (`OPCTree.changes.json`)

Se comparan solo los objetos que existen en SecurOS: los paneles y los ítems que tienen `objType` y
`securosId`. Cada objeto se identifica por `objType|securosId`. Su nombre es la propiedad OPC **101**
(`DetectorShortText`) o, si no la tiene, el `itemName`.

Ejemplo, formateado para leerlo (el archivo real se escribe en una sola línea):

```json
{
  "createdAt": "2026-10-07T12:30:00",
  "added":   [ { "ObjType": "TESTUPDATEFIREPANEL_DETECTOR", "ObjId": "1-1.8.31", "Name": "Detector oficina 3", "ParentId": "1-1", "IsPanel": false } ],
  "removed": [ { "ObjType": "TESTUPDATEFIREPANEL_NAC",      "ObjId": "1-1.14.4", "Name": "Sirena pasillo",     "ParentId": "1-1", "IsPanel": false } ],
  "renamed": [ { "ObjType": "TESTUPDATEFIREPANEL_DETECTOR", "ObjId": "1-1.8.3",  "OldName": "Detector 3", "NewName": "Detector sala reuniones" } ]
}
```

| Lista | Significado | Comando que genera |
|---|---|---|
| `removed` | Estaba en `OPCTree.json` y ya no está en el panel | `DELETE_OBJECT` |
| `added` | Está en el árbol releído y no en `OPCTree.json` | `CREATE_OBJECT` |
| `renamed` | Mismo `ObjType\|ObjId`, distinto nombre | `UPDATE_OBJECT` (solo `name`) |

## 3. Cambios en SecurOS

El orden de los comandos respeta la jerarquía panel → ítem:

1. **DELETE_OBJECT**, primero los ítems y después los paneles. Se elimina antes de crear porque un objid puede
   volver con otro tipo.
2. **CREATE_OBJECT**, primero los paneles y después los ítems, porque cada ítem necesita que su padre exista.
   - Un panel cuelga del punto de integración: `parent_id = idUinP`.
   - Un ítem cuelga del objid de su panel: `parent_id = ParentId`, por ejemplo `1-1`.
3. **UPDATE_OBJECT** con el nombre nuevo.

Ejemplos de mensajes:

```
CORE|1|DELETE_OBJECT|objtype<TESTUPDATEFIREPANEL_NAC>,objid<1-1.14.4>
CORE|1|CREATE_OBJECT|objtype<TESTUPDATEFIREPANEL_DETECTOR>,objid<1-1.8.31>,name<Detector oficina 3>,parent_id<1-1>
CORE|1|UPDATE_OBJECT|objtype<TESTUPDATEFIREPANEL_DETECTOR>,objid<1-1.8.3>,name<Detector sala reuniones>
```

- `<`, `>` y `,` delimitan los parámetros IIDK. Si un nombre los contiene, se reemplazan por espacio
  (`D,H MANEJADOR` → `D H MANEJADOR`).
- Al final, la consola muestra cuántos comandos se enviaron por acción, agrupados por el `rc` de `IidkSendMsg`.

## 4. Promoción del árbol

El árbol releído pasa a ser el activo, así el siguiente RESET compara contra el estado que ya se aplicó:

- `OPCTree.json` se copia a `OPCTree.previous.json`, como respaldo.
- `OPCTree.reset.json` se copia a `OPCTree.json`.

## 5. Recarga del monitoreo

- Se libera el grupo de suscripción OPC y se crea uno nuevo con el mapa del árbol nuevo. Así los ítems nuevos
  empiezan a reportar estado y los eliminados dejan de monitorearse.
- Se reemplaza el mapa de comandos, para que SecurOS pueda enviar comandos a los ítems nuevos.

## 6. Respuesta del RESET a SecurOS

Según el manual del IIDK, al recibir `Action:RESET` se responde con **un solo mensaje**:

```
<tipo raíz>|<Id del RESET>|RESET|<json>
```

| Parte | Valor |
|---|---|
| `<tipo raíz>` | Tipo de objeto raíz de la integración. Por defecto es `ddi_prefix` + `FIRE_PANEL` (por ejemplo `TESTUPDATEFIREPANEL_FIRE_PANEL`). Se puede cambiar con la clave `reset_response_type` de `App.config`. |
| `<Id del RESET>` | El `Id` que llegó en el RESET. Como solo se atiende el RESET de este punto de integración, siempre es `idUinP`. |
| `RESET` | La acción que se responde. |
| `<json>` | El árbol de objetos serializado. |

El JSON tiene `version` y la lista `objects`, y cada objeto lleva `type`, `id` y `name`:

```json
{
  "version": 1,
  "objects": [
    { "type": "NOTIFICATION_OUTPUT", "id": "1", "name": "NAC Piso 1" },
    { "type": "MONITOR_INPUT",       "id": "1", "name": "Entrada monitoreo 1" },
    { "type": "MONITOR_INPUT",       "id": "2", "name": "Entrada monitoreo 2" }
  ]
}
```

Los objetos salen del árbol activo (`OPCTree.json`) en `RespondToReset` (`Flows/OpcTreeResetFlow.cs`), con
`OpcTreeReader.ExtractObjectDefinitionsFromTree`: la misma lectura que usa la comparación del paso 2. Cuando se
responde, ese archivo ya es el árbol promovido o, si no hubo cambios, equivale al releído. Se envían los paneles
y sus ítems; `name` es el `itemName` de cada nodo.

No se responde con mensajes `SETUP` ni `STATE`: la respuesta es únicamente el mensaje `RESET` con el JSON.

### Cuándo se responde

La respuesta es **el último paso** y se envía solo cuando toda la actualización terminó:

| Resultado | ¿Se responde? |
|---|---|
| Relectura sin cambios | Sí |
| Cambios aplicados, árbol promovido y suscripción OPC recargada | Sí |
| Falla la relectura, la comparación, la escritura del reporte o la promoción | No |
| No se pudo recargar la suscripción OPC | No |

Si no se responde, la consola muestra:
`[RESET] La actualización no terminó — no se responde el RESET a SecurOS.`

## Probar sin esperar un RESET

En el modo `--test`, la opción **5 – Aplicar OPCTree.changes.json** envía los mismos DELETE/CREATE/UPDATE
del paso 3 a partir del último reporte. Muestra antes un resumen, permite elegir el alcance (todo, solo DELETE,
solo CREATE o solo UPDATE), tiene un modo **Simular** que muestra los mensajes sin enviarlos y pide confirmación.

## Limitaciones conocidas

1. **No se sabe qué `rc` significa éxito.** Los comandos rechazados por SecurOS se cuentan en el resumen, pero
   no impiden promover el árbol ni responder el RESET. Un CREATE que falló no se reintenta en el siguiente RESET.
2. **Tipos nuevos que SecurOS no conoce.** Si aparece un grupo cuyo tipo no está en el `.integration.json` ni en
   el `.ddi` desplegados, SecurOS rechaza el CREATE de sus ítems. Hay que regenerar y desplegar los archivos de
   integración con `--initial-setup`, lo que implica reiniciar el servicio. El RESET no lo hace.
3. **Primer RESET después del cambio a ids por grupo.** Un `OPCTree.json` generado con la numeración anterior
   (por posición) tiene otros ids. Antes de usar el RESET hay que regenerarlo con `--initial-setup`; si no, los
   objetos renumerados se borrarían y se volverían a crear.
4. **No se filtra por `allowed_types`.** La comparación lee el árbol completo, incluso tipos que no se
   despliegan en SecurOS (ver [allowed-types.md](allowed-types.md)).
5. **Tiempo de respuesta.** El `.integration.json` define `reset_timeout: 60`. La relectura completa y los
   comandos de un panel grande pueden acercarse a ese límite antes de que se envíe la respuesta.
