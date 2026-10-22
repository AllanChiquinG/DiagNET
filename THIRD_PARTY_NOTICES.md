# Avisos de licencias de terceros distribuidos con DiagNET

Verificado el 21 de octubre de 2026 contra los metadatos oficiales de nuget.org y el repositorio oficial de cada componente.
**Una actualización futura de cualquiera de estos componentes debe revisar de nuevo su licencia antes de cambiar de versión.**

## PDFsharp y MigraDoc 6.2.4 — licencia MIT

- Paquetes: `PDFsharp-MigraDoc` 6.2.4 (incluye MigraDoc) y `PDFsharp` 6.2.4. Proyecto: https://github.com/empira/PDFsharp
- Copyright © 2026 empira (según los metadatos del paquete).
- **Versión fijada** (`[6.2.4]` en `src/Servidor.Integracion/Servidor.Integracion.csproj`). Las versiones 6.5.0 y 7.0.0-preview publicadas en nuget.org declaran **AGPL-3.0-only**: no se deben adoptar sin una decisión expresa y una nueva revisión de licencia.

```text
MIT License

Copyright (c) empira

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Liberation Sans 2.1.5 — SIL Open Font License 1.1

- Archivos incrustados sin modificar: `LiberationSans-Regular.ttf`, `-Bold.ttf`, `-Italic.ttf`, `-BoldItalic.ttf` en `src/Servidor.Integracion/Pdf/Fuentes/`, junto con `LICENSE` (texto completo de la OFL 1.1) y `AUTHORS`.
- Copyright © 2010 Google Corporation (con las fuentes reservadas Arimo, Tinos y Cousine) y © 2012 Red Hat, Inc. (nombre de fuente reservado «Liberation»).
- Origen: https://github.com/liberationfonts/liberation-fonts, archivo `liberation-fonts-ttf-2.1.5.tar.gz`, SHA-256 `7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0`.
- Condiciones: la fuente no se vende por sí sola, se distribuye con su licencia y no se modifica ni se renombra.

## System.Security.Cryptography.Pkcs 10.0.13 — licencia MIT (Microsoft / .NET Foundation)

Dependencia transitiva de PDFsharp, fijada a una versión sin avisos de seguridad.
