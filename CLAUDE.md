# Instrucciones para Claude Code

## Principio de Responsabilidad Única (SRP)

Aplicar siempre en todo código C# generado:

- Cada método hace **una sola cosa**, descrita exactamente por su nombre.
- Si un método supera **20-30 líneas**, dividirlo en submétodos privados.
- Cada submétodo tiene nombre descriptivo que explica lo que hace (ej: `ValidarEntrada`, `ConstruirRespuesta`, `CalcularTotal`).
- El método principal se lee como una secuencia de pasos de alto nivel, delegando detalles a submétodos.
- Refactor puro: no cambiar comportamiento externo, solo estructura interna.
