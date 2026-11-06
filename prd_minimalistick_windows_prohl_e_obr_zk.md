# PRD: Minimalistický Windows prohlížeč obrázků (FastStone Style)

## 1. Cíl projektu
Vytvořit extrémně rychlý, lehký a nativní desktopový prohlížeč obrázků pro Windows s minimální latencí při spuštění z Průzkumníku Windows, inspirovaný ovládáním a chováním **FastStone Image Viewer**. Důraz je kladen na čistý fullscreen bez rušivých prvků, okamžitou odezvu a bleskové klávesové zkratky.

## 2. Tech stack a architektura
* **Jazyk / Framework:** **C# (.NET 9 WPF)** – nativní Windows aplikace s hardwarovou akcelerací (DirectX), bleskovým studeným startem a přímým přístupem k Windows API (Koš, Schránka, asociace souborů).
* **Dekódování a zpracování obrazu:** Nativní `WIC (Windows Imaging Component)` přes WPF `BitmapImage` / `TransformedBitmap` (vysoká rychlost, nativní podpora JPG, PNG, WEBP, GIF + automatická korekce EXIF orientace) doplněné o `SixLabors.ImageSharp` pro vícevláknovou dávkovou konverzi a změnu rozměrů.
* **Distribuce:** Samostatný spustitelný soubor (`FastImageViewer.exe`) připravený pro asociaci s obrazovými formáty ve Windows (`Open With`).

---

## 3. Fázovaný plán vývoje (Milníky – Hotovo)

### Fáze 1: Základní prohlížeč a FastStone navigace (MVP)
* **Chování po spuštění:**
  * Při otevření konkrétního obrázku z Průzkumníku Windows (předání cesty v argumentu příkazové řádky) se aplikace otevře **okamžitě v čistém fullscreen režimu** (bez okrajů, panelů a OSD).
  * Při spuštění samotné aplikace bez argumentu se otevře přehled (**Overview**) v naposledy otevřené složce (případně ve složce *Obrázky*).
* **Přepínání pohledů a ukončení:**
  * Klávesa `Enter` (nebo dvojklik myší) přepíná mezi **Fullscreenem** a **Overview**.
  * Klávesa `Esc` ve Fullscreenu vrací uživatele zpět do **Overview**; v režimu Overview klávesa `Esc` aplikaci ukončí.
* **Podporované formáty:** `JPG`, `JPEG`, `JFIF`, `PNG`, `WEBP`, `GIF`, `BMP`, `TIF`, `TIFF`, `ICO`, `HEIC`, `HEIF`, `AVIF`.
* **Navigace ve Fullscreenu:**
  * Plynulý posun mezi obrázky v aktuální složce: `Šipka vpravo` / `Šipka dolů` / `Mezerník` / `PageDown` (další), `Šipka vlevo` / `Šipka nahoru` / `Backspace` / `PageUp` (předchozí), `Home` / `End` (první / poslední).
  * Řazení souborů ve složce odpovídá přirozenému řazení podle názvu ve Windows Exploreru (`StrCmpLogicalW` natural sort, např. `img2.jpg` před `img10.jpg`).
* **Výkon a kešování:**
  * In-memory cache (LRU) a asynchronní přednačítání (**prefetching**) sousedních snímků (+/- 2 snímky dopředu i dozadu) pro nulovou prodlevu při listování.

---

### Fáze 2: Zoom, lupa, správa souborů a mazání
* **Zoom a ovládání ve Fullscreenu:**
  * **Výchozí zobrazení:** *Fit to Window* (zmenšení velkých obrázků na velikost obrazovky se zachováním poměru stran; menší obrázky v 1:1).
  * **FastStone Lupa (100 % náhled):** Stisk a držení **levého tlačítka myši** (ve výchozím zobrazení) okamžitě přepne obrázek na 100 % (1:1) v místě kurzoru a tažením myši umožňuje plynulý posun (pan). Po uvolnění tlačítka se obraz vrátí zpět na *Fit to Window*.
  * **Kolečko myši (přepínatelné, výchozí = listování fotkami):**
    * Ve výchozím režimu kolečko myši plynule listuje mezi předchozím a dalším snímkem.
    * Podržení `Ctrl + kolečko myši` vždy provádí plynulý zoom přímo na pozici kurzoru.
    * Klávesou `M` (nebo tlačítkem v liště) lze chování kolečka trvale přepnout mezi režimem **Listování fotkami** a **Zoom na kurzor**.
  * **Plynulý zoom a rychlé měřítko:** Klávesy `+` / `-`, `*` pro 100 % (1:1) a `/` pro *Fit to Window*.
* **Kopírování souboru (`C`):**
  * Stisk klávesy `C` vyvolá modální dialog s předvyplněnou poslední použitou cílovou složkou a okamžitým potvrzením klávesou `Enter`.
* **Mazání souboru (`Delete`):**
  * Rychlé a tiché smazání aktuálního souboru **přímo do Koše Windows** (`SHFileOperation` s `FOF_ALLOWUNDO | FOF_NOCONFIRMATION`) a okamžitý posun na další obrázek ve složce.

---

### Fáze 3: Transformace, výstřižky a výsuvný panel ve Fullscreenu
* **Výsuvný levý ovládací panel ve Fullscreenu (FastStone / TeamViewer styl):**
  * Při najetí myší k levému okraji obrazovky (`X <= 14 px`) vyjede ovládací panel s informacemi o fotce a tlačítky pro všechny funkce. Po opuštění panelu myší se automaticky schová.
* **Transformace s odloženým uložením při odchodu z fotky:**
  * Klávesové zkratky `L` (rotace o 90° doleva), `R` (rotace o 90° doprava), `H` (horizontální zrcadlení), `V` (vertikální zrcadlení) otočí fotku okamžitě v paměti.
  * Při přechodu na další fotku, stisku `Esc` nebo `Enter` vyskočí dialog s volbou: **Uložit jako novou (`_rotated`, výchozí `Enter`)**, **Přepsat originál (`P`)**, **Neukládat (`D`)** nebo **Zrušit (`Esc`)**.
* **Interaktivní výstřižek / Crop (`X`):**
  * Výběr oblasti tažením myši se třetinovou mřížkou (Rule of Thirds), 8 úchyty pro doladění velikosti za rohy i hrany, posunem výřezu myší, živým rozměrem v px vlevo nahoře a tlačítkem `✓ OK (Enter)` vpravo nahoře.
  * Po potvrzení dialog umožní **Uložit do souboru (`_cropped`, výchozí `Enter`)** nebo **Zkopírovat do schránky (`C`)**.
* **Režim ukládání úprav (`U`):**
  * Přepínač mezi automatickým ukládáním se sufixem (`_rotated` / `_cropped`) bez dialogu a klasickým dialogem „Uložit jako…“.

---

### Fáze 4: Dávková konverze (Batch Convert) a třípanelový Overview
* **Dávková konverze a změna rozměrů (`B` / `F3`):**
  * Vstupní soubory z označených fotek v Overview, celé složky nebo přes Drag & Drop.
  * Výstupní formáty `JPG`, `PNG`, `WEBP` s posuvníkem kvality (1–100 %).
  * Volitelná změna rozměrů se zachováním poměru stran: předvolby delší strany v px (`3840`, `2560`, `1920`, `1600`, `1280`, `800` nebo `Vlastní`) i procent (`20 %`, `40 %`, `50 %`, `60 %`, `80 %` nebo `Vlastní %`).
  * Vícevláknové zpracování (`Parallel.ForEachAsync`) s ukazatelem průběhu a možností přerušení.
* **Třípanelový režim Overview ve stylu FastStone:**
  * **Vlevo nahoře:** Strom složek (`TreeView`) s líným načítáním podsložek, rozbalením na jedno kliknutí a plynulou navigací šipkami i myší.
  * **Vlevo dole:** Dynamický zvětšený náhled právě označené fotky s rozlišením a velikostí souboru (kliknutím přepne do Fullscreenu).
  * **Vpravo:** Mřížka miniatur s asynchronním načítáním od aktivní fotky ven.
  * **Posuvné dělicí příčky (`GridSplitter`):** Nastavená šířka levého panelu i výška náhledu se automaticky ukládají.
