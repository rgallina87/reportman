# SKILL — esta bifurcación de reportman (Intelisis Hub)

**Qué es este repositorio.** Una bifurcación de [tonimartir/reportman](https://github.com/tonimartir/reportman),
el motor de Report Manager en C#, que **Intelisis Hub v2 compila del fuente** para rendir reportes y tickets.
No es un producto aparte ni un servicio: es una **biblioteca** que termina dentro del motor.

**Por qué existe la bifurcación.** Probando contra una Epson TM-T88V real hubo que cambiar dos cosas de este
código. Un submódulo apuntando al repositorio del autor no puede recibir commits nuestros, así que los cambios
vivían como parches sueltos que había que recordar aplicar; quien no lo hacía obtenía tickets sin corte y sin
logo, sin ninguna pista del motivo. Con la bifurcación viajan solos en cada clon.

**Licencia.** MPL 1.1 (dual con GPL), la del original. Permite modificar y **exige publicar la modificación**:
por eso este repositorio es público y los cambios están commiteados, no escondidos en un binario.

## Cómo lo consume Intelisis Hub

```
intelisishubv2/
└── Capa0-Engine/
    ├── external/reportman/          ← este repositorio, submódulo, rama `intelisis`
    └── IntelisisAPI/IntelisisAPI.csproj
          <ProjectReference Include="..\external\reportman\Reportman.Drawing\...">
          … y Reportman.Reporting, Drawing.CrossPlatform, Reporting.Design, Design.Json
```

Al clonar el Hub: `git clone --recurse-submodules`. En un clon que ya existía y venía del original:

```bash
git submodule sync && git submodule update --init --recursive
```

**Nada que instalar en un servidor.** Esto se compila dentro del motor; en producción ya viaja adentro.

## La rama `intelisis`

Es donde vive lo nuestro, partiendo de `78731f5` del original. Regla: **un commit por cambio, explicando el
síntoma físico** que lo motivó, porque quien lo lea después va a estar frente a una impresora que hace algo raro.

Lo que lleva hoy:

| Cambio | Síntoma que resolvió |
|---|---|
| Corte de papel (`ESC d 4` + `GS V 66 0`) en los TRES finales de documento | el ticket salía y se quedaba colgando, sin cortar |
| `MetaObjectType.Image` → `NativeImageOut`, con el rasterizador inyectado desde fuera (`PrintOutText.ImageRasterizer`) | un ticket no podía llevar logo: las imágenes no se dibujaban |
| `Reportman.Drawing.CrossPlatform/EscPosImagen.cs` (nuevo) | el logo, rasterizado con SkiaSharp y difuminado Floyd–Steinberg, que es lo que lo hace legible en una térmica de dos tonos |
| `tests/EscPosReceiptTest` | espera la secuencia de corte nueva |
| `Reportman.Drawing.Excel/PrintOutClosedExcel.cs` (2026-09-25) | el Excel del Hub (`FormatoReporte.Excel`): el exportador solo escribía a archivo; tomaba cualquier cosa «numérica» por número (un EAN o un folio con ceros perdían dígitos, «1,2,3» era número) y leía fechas con un parse libre y ambiguo (05/09 = mayo o septiembre según el servidor) que además solo aceptaba 8–10 caracteres; un texto de los datos que empezaba con `=` quedaba **sin** neutralizar porque ClosedXML se come el apóstrofo inicial, y `+ - @` tab o CR ni se miraban; una imagen ilegible abortaba el libro. Ahora: salida a `ResultStream`, `SheetName`, números estrictos por cultura con el formato que se imprimió (decimales, miles, moneda, %, paréntesis), códigos como texto, fechas por patrones exactos (`DatePatterns`), textos peligrosos con apóstrofo literal + `quotePrefix`, anchos de columna del diseño |
| `Reportman.Drawing.CrossPlatform/FontInfoFt.cs`: `SetPara(línea, 254)` (2026-09-25) | en TODOS los PDF, un texto sin letras (solo números y signos) salía con sus grupos invertidos: «2026-09-12 04:30» → «04:30 2026-09-12», «-45.25» → «45.25-», «(1)» → «)1(», también la fecha del CFDI carta. El ActualText seguía bien, así que copiar y pegar no lo delataba. El nivel de párrafo era 255 (UBIDI_DEFAULT_RTL: sin carácter fuerte = RTL); ahora es 254 (UBIDI_DEFAULT_LTR). Un texto árabe o hebreo sigue siendo RTL por su primera letra |

## Reglas al tocar este repositorio

1. **El motor gráfico no entra aquí.** `Reportman.Drawing` no debe depender de SkiaSharp: el rasterizador se
   inyecta (`PrintOutText.ImageRasterizer`). Quien rompa eso ata la biblioteca a un backend gráfico concreto.
2. **Cambiar esto obliga a mover el pin** en el Hub: `git add Capa0-Engine/external/reportman` y commit allá.
   Un cambio que no mueve el pin no lo ve nadie más.
3. **Traer lo del autor original**: `git fetch origin && git rebase origin/master` (o merge) sobre `intelisis`.
   El remoto `origin` es el del autor; `intelisis` es esta bifurcación.
4. **Probar contra hardware.** ESC/POS no se valida leyendo: el test de la secuencia ayuda, pero lo que manda es
   que salga el papel. Los dos cambios de arriba se probaron en una TM-T88V con el agente de periféricos.

## Dónde está el resto

La documentación del reporteador como parte del producto —tipos de plantilla, usos por movimiento, el diseñador,
ESC/POS, PDF y PNG— vive en el Hub, en `skill/reportes-hub.md`. Aquí solo está lo que es propio de esta biblioteca.
