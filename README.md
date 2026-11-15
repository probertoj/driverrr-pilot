<p align="center">
  <img src="Driverrr/Assets/logo.png" width="120" alt="Logo de Driverrr">
</p>

<h1 align="center">Driverrr</h1>

<p align="center">Mantiene al día los drivers de tu PC con Windows, gratis y solo con fuentes oficiales de Microsoft.</p>

---

> **Estado: versión temprana.** Funciona, pero se ha probado en un solo equipo. Actualizar drivers siempre
> tiene riesgo; Driverrr crea un punto de restauración y una copia antes de cada cambio, pero úsalo bajo tu
> responsabilidad y empieza por un driver poco crítico.

## Qué hace

- **Escanea** los dispositivos del equipo y el driver que usa cada uno.
- **Busca versiones más nuevas** en dos fuentes de Microsoft:
  - **Windows Update**, incluidos los drivers «opcionales» que Windows no instala por su cuenta.
  - **Catálogo de Microsoft Update**, que contiene todas las versiones certificadas de cada driver.
- **Instala** con un clic, uno a uno o todos a la vez.
- **Protege antes de tocar nada**: punto de restauración del sistema y copia del driver actual.
- **Restaura** el driver anterior desde la propia app si el nuevo no te convence.

No usa bases de datos de terceros ni descarga nada que no venga de servidores de Microsoft.

## Descarga y uso

1. Descarga `Driverrr.exe` desde la página de [Releases](../../releases).
2. Ábrelo. No necesita instalar nada más: lleva dentro todo lo necesario.
3. Pulsa **Escanear**.
4. (Recomendado) Pulsa **Instalar en este equipo**, abajo a la izquierda. Lo copia a `Archivos de programa`,
   crea un acceso directo en el menú Inicio y permite desinstalarlo desde Configuración > Aplicaciones.

**Requisitos:** Windows 10 u 11 de 64 bits y una cuenta con permisos de administrador (Windows los pedirá al
instalar o restaurar un driver).

**Aviso de SmartScreen:** el ejecutable no está firmado digitalmente, así que Windows puede mostrar «Windows
protegió su PC» la primera vez. Es lo habitual en programas pequeños sin certificado de pago. Si prefieres no
fiarte de un binario, compílalo tú mismo desde el código (más abajo).

## Cómo decide qué es una actualización

Un driver solo se ofrece si:

- su versión es mayor **y** su fecha no es anterior a la del instalado (Windows ordena los drivers por fecha
  antes que por versión, y algunos fabricantes cambian de numeración);
- está publicado para tu versión de Windows;
- en el caso del Catálogo, corresponde al identificador de hardware de tu dispositivo.

Aun así, la última palabra la tiene Windows: si considera que tu driver actual es mejor, no lo cambia, y
Driverrr te lo dice en lugar de dar la actualización por hecha.

## Seguridad

Instalar drivers exige permisos de administrador, así que la app está pensada para que un programa sin
permisos no pueda aprovecharse de ella:

- **Solo descarga de Microsoft**, por HTTPS, comprobando el servidor final tras cualquier redirección.
- **Verifica la huella SHA-256** de cada paquete contra la que publica el Catálogo.
- **Exige firma de Microsoft** (certificación WHQL) en el paquete, además de la validación del propio Windows.
- **Comprueba que el paquete es para tu dispositivo exacto** y tu arquitectura antes de instalarlo.
- **Vuelve a consultar el Catálogo en vivo** antes de instalar: lo guardado en caché no se da por bueno.
- **Trabaja en carpetas protegidas** (`C:\ProgramData\Driverrr`), donde solo un administrador puede
  escribir, y rechaza enlaces y carpetas creadas por otros programas.
- **No acepta licencias en tu nombre** y **no instala sin copia** del driver anterior.
- Llama a las herramientas de Windows por su ruta completa en `System32`.

Si la usas sin instalar, desde una carpeta como Descargas, cualquier programa podría modificar el ejecutable.
Por eso se recomienda el paso «Instalar en este equipo».

## Privacidad

Driverrr no tiene servidor propio, cuentas ni telemetría. Las únicas conexiones que hace son a:

- el servicio de Windows Update de tu equipo;
- `catalog.update.microsoft.com`, a la que envía identificadores de hardware genéricos (por ejemplo
  `PCI\VEN_10EC&DEV_8168`, que identifica un modelo de chip, no tu equipo).

Guarda en tu PC una caché de consultas (`%LocalAppData%\Driverrr`) y un registro de lo que instala
(`C:\ProgramData\Driverrr`). Nada de eso sale de tu equipo.

## Limitaciones conocidas

- **El Catálogo limita las consultas seguidas.** Un primer escaneo puede quedar incompleto; la app lo avisa y
  basta con volver a escanear a los pocos minutos.
- La búsqueda en el Catálogo lee su página web, no una API oficial: si Microsoft cambia el formato, esa fuente
  dejará de dar resultados hasta que se actualice la app. Windows Update seguiría funcionando.
- El Catálogo devuelve como mucho 25 resultados por consulta.
- Un paquete del Catálogo puede contener drivers para más de un dispositivo; Windows los aplica a los que
  correspondan.
- Las tarjetas gráficas suelen tener versiones más nuevas en la web del fabricante que en Microsoft; la app
  enlaza a la web oficial de NVIDIA, AMD o Intel en esos casos.
- Interfaz solo en español y sin tema oscuro.

## Si la ventana sale en blanco

Suele ser un problema del driver gráfico del equipo, no de la app. Ábrela dibujando sin la tarjeta gráfica:

```bash
Driverrr.exe --sin-aceleracion
```

## Volver atrás

En la página **Restaurar** aparecen los drivers que has cambiado con Driverrr. Al restaurar, la app retira
el driver nuevo para que Windows recupere el anterior (y lo repone desde la copia si ya no estuviera).
Reinstalar el antiguo sin más no funciona, porque Windows elige siempre el más reciente.

También puedes usar el punto de restauración que se crea antes de cada cambio, o «Revertir al controlador
anterior» en el Administrador de dispositivos.

## Compilar desde el código

Necesitas el [SDK de .NET 9](https://dotnet.microsoft.com/download).

```bash
dotnet build Driverrr/Driverrr.csproj -c Release
```

Para generar el ejecutable único que se distribuye:

```bash
dotnet publish Driverrr/Driverrr.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

Pruebas (no instalan nada ni necesitan administrador):

```bash
dotnet run --project Driverrr.Tests -c Release
```

## Licencia

[MIT](LICENSE). Sin garantía de ningún tipo.

Driverrr no está afiliado a Microsoft ni a ningún fabricante de hardware. Windows, Windows Update y
Microsoft Update Catalog son marcas de Microsoft Corporation.
