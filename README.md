# FastImageViewer ⚡🖼️

Extrémně rychlý, lehký a nativní desktopový prohlížeč obrázků pro **Windows 10 / 11**, silně inspirovaný ovládáním a chováním **FastStone Image Viewer**.

Navržený s důrazem na **okamžitý start z Průzkumníku Windows**, čistý bezokrajový Fullscreen bez rušivých prvků, bleskovou odezvu a intuitivní klávesové zkratky.

---

## ✨ Hlavní přednosti a funkce

* **⚡ Okamžitý čistý Fullscreen:** Při otevření fotky z Průzkumníku Windows (*Otevřít v programu…* / dvojklik) se aplikace otevře přímo v čistém fullscreenu bez jakýchkoliv lišt a okrajů.
* **🗂️ Třípanelový režim Overview (styl FastStone):**
  * **Vlevo nahoře – Strom složek:** Okamžité přepínání mezi disky a složkami jedním kliknutím nebo šipkami na klávesnici.
  * **Vlevo dole – Živý náhled:** Zvětšený dynamický náhled právě označené fotky včetně rozlišení a velikosti v KB/MB (kliknutím na náhled přejdete do Fullscreenu).
  * **Vpravo – Mřížka miniatur:** Rychlé paralelní načítání miniatur s přirozeným řazením jako ve Windows Exploreru (`StrCmpLogicalW`, např. `2.jpg` před `10.jpg`).
  * **Resizovatelné panely:** Šířku levého panelu i výšku živého náhledu lze plynule měnit tažením dělicích příček (rozložení se automaticky ukládá).
* **🔍 FastStone Lupa (100 % náhled 1:1):**
  * Stisk a držení **levého tlačítka myši** ve Fullscreenu okamžitě přiblíží fotku na **100 % (1:1)** v místě kurzoru a tažením myši umožňuje plynulý posun (*pan*).
  * Po uvolnění tlačítka myši se obraz ihned vrátí zpět na celou obrazovku (*Fit to Window*).
* **🎛️ Výsuvný ovládací panel ve Fullscreenu (styl FastStone / TeamViewer):**
  * Při najetí myší k **levému okraji obrazovky** vyjede přehledný ovládací panel se všemi funkcemi a informacemi o fotce. Po opuštění panelu myší se automaticky schová.
* **🔄 Rotace a zrcadlení (`L`, `R`, `H`, `V`) s chytrým ukládáním:**
  * Otočení proběhne okamžitě v paměti. Při přechodu na další fotku, stisku `Esc` nebo `Enter` se zobrazí dialog, zda chcete fotku uložit jako novou (`originalname_rotated`, výchozí pod `Enter`), nebo přepsat originál (`P`).
* **✂️ Interaktivní výstřižek / Crop (`X`):**
  * Tažením myší vyberete oblast s **třetinovou mřížkou (Rule of Thirds)**, živým rozměrem v pixelech a **8 úchyty** (rohy i hrany) pro dodatečné doladění velikosti nebo posunutí výřezu.
  * Potvrzení klávesou `Enter` uloží výřez jako `originalname_cropped` nebo klávesou `C` zkopíruje výřez přímo do **schránky Windows (Clipboard)** pro okamžité `Ctrl+V`.
* **💾 Volitelný režim ukládání úprav (`U`):**
  * Kdykoliv lze přepnout mezi automatickým ukládáním se sufixem (`_rotated` / `_cropped`) bez zdržování a klasickým systémovým dialogem *„Uložit jako…“*.
* **⚙️ Vícevláknová dávková konverze a změna rozměrů (`B` / `F3`):**
  * Hromadný převod vybraných fotek nebo celé složky (včetně *Drag & Drop*) do formátů `JPG`, `PNG`, `WEBP`, `BMP` nebo `TIFF`.
  * Nastavení kvality komprese (1–100 %) a změna rozměrů se zachováním poměru stran (předvolby delší strany v px: `3840`, `2560`, `1920`, `1600`, `1280`, `800` nebo procenta: `20 %`, `40 %`, `50 %`, `60 %`, `80 %`, případně vlastní hodnota).
* **🖼️ Široká podpora formátů (13 přípon):**
  * `JPG`, `JPEG`, `JFIF`, `PNG`, `WEBP`, `GIF`, `BMP`, `TIF`, `TIFF`, `ICO`, `HEIC`, `HEIF`, `AVIF`.
* **📁 Blesková správa souborů:**
  * **Kopírování (`C`):** Rychlý dialog s předvyplněnou poslední cílovou složkou (stačí stisknout `C` a `Enter`).
  * **Mazání (`Delete`):** Tichý přesun aktuální fotky přímo do **Koše Windows** a okamžitý přechod na další snímek.
* **🚀 In-memory LRU Cache & Prefetching:** Automatické přednačítání sousedních fotek na pozadí pro nulovou prodlevu při listování.

---

## ⌨️ Přehled klávesových zkratek a ovládání

| Klávesa / Myš | Akce |
| :--- | :--- |
| **`Enter`** nebo **Dvojklik myší** | Přepnutí mezi **Fullscreenem** a **Overview** (mřížkou miniatur) |
| **`Esc`** (ve Fullscreenu) | Návrat z Fullscreenu do **Overview** (nebo ukončení režimu výstřižku) |
| **`Esc`** (v Overview) | Ukončení aplikace |
| **Držení levého tlačítka myši** | **FastStone Lupa (100 % 1:1)** v místě kurzoru + posun tažením myši |
| **Myš k levému okraji obrazovky** | Vysunutí bočního ovládacího panelu ve Fullscreenu |
| **`←` / `↑` / `PageUp` / `Backspace`** | Předchozí fotka |
| **`→` / `↓` / `PageDown` / `Mezerník`** | Další fotka |
| **`Home` / `End`** | První / poslední fotka ve složce |
| **`Kolečko myši`** | Listování mezi fotkami *(výchozí)* nebo Zoom *(přepíná se klávesou `M`)* |
| **`Ctrl + Kolečko myši`** | Plynulý zoom přímo na pozici kurzoru |
| **`+` / `-`** | Přiblížit / Oddálit |
| **`*`** | Přepnout zobrazení **100 % (1:1)** |
| **`/`** nebo **`0`** | Přizpůsobit oknu (*Fit to Window*) |
| **`L` / `R`** | Otočit o 90° doleva / doprava |
| **`H` / `V`** | Převrátit horizontálně / vertikálně |
| **`X`** | Aktivovat interaktivní **Výstřižek (Crop)** |
| **`U`** | Přepnout režim ukládání úprav (*Auto sufix `_rotated`/`_cropped`* vs. *Dialog Uložit jako…*) |
| **`M`** | Přepnout režim kolečka myši (*Listování fotkami* vs. *Zoom*) |
| **`C`** | Kopírovat aktuální fotku do vybrané složky |
| **`B`** nebo **`F3`** | Otevřít **Dávkovou konverzi a změnu rozměrů** |
| **`Delete`** | Přesunout aktuální fotku do **Koše Windows** |
| **`O`** nebo **`Ctrl + O`** | Otevřít jiný soubor / složku |

---

## 📦 Stažení a spuštění (včetně tipu pro firemní PC)

Nejnovější verzi najdete vždy v sekci **[Releases](https://github.com/h4gh/App_FastImageViewer/releases)**. Každý release je automaticky sestaven přímo na serverech GitHubu pomocí **GitHub Actions** a obsahuje 3 varianty:

1. **`FastImageViewer-win-x64-portable.zip` *(Doporučeno pro firemní PC s přísnou IT politikou)***
   * Obsahuje aplikaci i kompletní .NET 9 runtime ve standardní složce (nic nerozbaluje do `%TEMP%`, takže neaktivuje falešné poplachy firemního AppLockeru nebo EDR/antiviru).
   * 💡 **Tip pro spuštění v práci bez varování Windows SmartScreen:**
     1. Stáhněte `FastImageViewer-win-x64-portable.zip`.
     2. **Před rozbalením** klikněte na stažený `.zip` soubor pravým tlačítkem myši -> **Vlastnosti (Properties)**.
     3. Úplně dole zaškrtněte **„Odblokovat“ (Unblock)** a klikněte na **OK**.
     4. Nyní `.zip` rozbalte do libovolné složky a spusťte `FastImageViewer.exe`.
2. **`FastImageViewer-win-x64-light-net9.zip` *(Ultralehký `.zip` – cca 1 MB)***
   * Minimalistická verze zabalená v `.zip` archivu (pro bezproblémové stažení přes firemní proxy a možnost *Odblokovat / Unblock* před rozbalením). Uvnitř je jediný ~2,3MB soubor `FastImageViewer.exe` pro počítače, kde je již nainstalován [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
3. **`FastImageViewer.exe` *(Samostatný Single-File)***
   * Vše zabalené v jediném `.exe` souboru (~60 MB), nevyžaduje instalaci .NET 9.
4. **`FastImageViewer_Light_net9.exe` *(Ultralehký Single-File)***
   * Jediný `.exe` soubor (~2.3 MB) pro počítače s nainstalovaným .NET 9 Desktop Runtime.

---

## 🛠️ Sestavení ze zdrojového kódu

Projekt je postaven na **.NET 9 (WPF)** a využívá nativní **WIC (Windows Imaging Component)** a knihovnu **SixLabors.ImageSharp**.

```powershell
# Běžné sestavení (Release)
dotnet build -c Release

# Publikování přenosné verze
dotnet publish -c Release -r win-x64 --self-contained true
```
