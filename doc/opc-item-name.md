# Origen del campo `itemName` en `OPCTree.json`

`itemName` **no lo calcula la aplicación**: es el **Item ID de OPC** que entrega el servidor OPC de Bosch al
recorrer su árbol, el identificador completo con el que el servidor reconoce cada elemento.

## Cómo se obtiene (`L3_Infrastructure/OpcDaClient.cs`)

1. **`BrowseLevel(parentId)`** llama a `_opcDaServer.Browse(...)`. El servidor devuelve los elementos del nivel
   como `BrowseElement`, cada uno con:
   - `Name`: nombre corto dentro de su nivel (ej. `22`).
   - `ItemName`: Item ID completo (ej. `Fire Panel 1-1.Bypass Group.22`).
2. **`MergeElementsByName`** une los elementos con el mismo `Name`. Un mismo nombre puede llegar dos veces: como
   rama (con hijos) y como ítem (con valor). Guarda `BranchItemName` (de la rama) e `ItemItemName` (del ítem).
3. **`ResolveItemName`** elige el valor final, prefiriendo el del ítem:
   ```csharp
   element.ItemItemName ?? element.BranchItemName ?? ""
   ```
4. **`CreateBaseNodeFields`** lo escribe en el nodo como `"itemName"`.

Excepción: el nodo raíz lleva `itemName: ""`, fijado en `BuildTree`.

## Ejemplos

| `name` (corto) | `itemName` (Item ID del servidor) |
|---|---|
| `CMD` | `CMD` |
| `Fire Panel 1-1` | `Fire Panel 1-1` |
| `22` | `Fire Panel 1-1.Bypass Group.22` |
| `CAN Bus` | `Fire Panel 1-1.CAN Bus` |

En los niveles superiores coinciden; más abajo, `itemName` es la ruta completa separada por puntos.

## Para qué se usa

| Uso | Dónde |
|---|---|
| Suscripción OPC: clave con la que se suscribe cada ítem y se reciben sus cambios | `nodeIdMap`: `itemName → objType|securosId` |
| Comandos hacia el panel: se añade al XML como `Adresse="<itemName>"` | `ExecuteSecurOsCommandUseCase` |
| Navegar a los hijos: el `itemName` de una rama es el punto de partida del siguiente `Browse` | `AttachChildrenIfBranch` |
| Nombre en SecurOS, solo si el ítem no tiene la propiedad 101 (`DetectorShortText`) | `OpcTreeReader.CollectItemObjectDefinitions` |

## Consecuencia práctica

Si en la programación del panel se cambia el nombre de una rama (por ejemplo `Bypass Group`), cambian los
`itemName` de todos sus hijos. Dejarán de coincidir con la suscripción y los comandos hasta volver a ejecutar
`--initial-setup`.
