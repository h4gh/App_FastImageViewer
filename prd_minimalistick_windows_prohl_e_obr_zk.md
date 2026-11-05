# PRD: Minimalistický Windows prohlížeč obrázků (FastStone Style)

## 1. Cíl projektu
Vytvořit extrémně rychlý, lehký a nativní desktopový prohlížeč obrázků pro Windows s minimální latencí při spuštění z Průzkumníku Windows, inspirovaný ovládáním a chováním **FastStone Image Viewer**. Důraz je kladen na čistý fullscreen bez rušivých prvků, okamžitou odezvu a bleskové klávesové zkratky.

## 2. Tech stack a architektura
* **Jazyk / Framework:** **C# (.NET 8/9 WPF)** – nativní Windows aplikace s hardwarovou akcelerací (DirectX), bleskovým studeným startem a přímým přístupem k Windows API (Koš, Schránka, asociace souborů).
* **Dekódování a zpracování obrazu:** Nativní `WIC (Windows Imaging Component)` přes WPF `BitmapImage` / `TransformedBitmap` (vysoká rychlost, nativní podpora JPG, PNG, WEBP, GIF) s možností doplnění o `SkiaSharp` nebo `ImageSharp` v pokročilých fázích (dávková konverze).
* **Distribuce:** Samostatný, lehký spustitelný soubor (`.exe`) připravený pro asociaci s obrazovými formáty ve Windows (`Open With`).

---

## 3. Fázovaný plán vývoje (Milníky)

### Fáze 1: Základní prohlížeč a FastStone navigace (MVP)
* **Chování po spuštění:**
  * Při otevření konkrétního obrázku z Průzkumníku Windows (předání cesty v argumentu příkazové řádky) se aplikace otevře **okamžitě v čistém fullscreen režimu** (bez okrajů, panelů a OSD).
  * Při spuštění samotné aplikace bez argumentu se otevře mřížkový náhled (**Overview**) v naposledy otevřené složce (případně ve složce *Obrázky*) nebo dialog pro výběr souboru/složky.
* **Přepínání pohledů a ukončení:**
  * Klávesa `Enter` (nebo dvojklik myší) přepíná mezi **Fullscreenem** a **Overview** (mřížkou miniatur celé aktuální složky).
  * Klávesa `Esc` ve Fullscreenu (pokud není otevřený žádný modální dialog ani aktivní výběr výstřižku) aplikaci rovnou ukončí (případně v režimu Overview také ukončí aplikaci).
* **Režim Overview (Mřížka miniatur):**
  * Maximální minimalismus – čistá mřížka miniatur pouze pro aktuální složku (bez bočního stromu složek).
  * Asynchronní načítání miniatur na pozadí, plynulé scrollování, navigace šipkami s viditelným označením aktivního snímku.
* **Podporované formáty:** `JPG / JPEG`, `PNG`, `WEBP`, `GIF` (statický).
* **Navigace ve Fullscreenu:**
  * Plynulý posun mezi obrázky v aktuální složce: `Šipka vpravo` / `Šipka dolů` / `Mezerník` / `PageDown` (další), `Šipka vlevo` / `Šipka nahoru` / `Backspace` / `PageUp` (předchozí), `Home` / `End` (první / poslední).
  * Řazení souborů ve složce odpovídá přirozenému řazení podle názvu ve Windows Exploreru (natural sort, např. `img2.jpg` před `img10.jpg`).
* **Výkon a kešování:**
  * In-memory cache (LRU) a asynchronní přednačítání (**prefetching**) sousedních snímků (+/- 2 snímky dopředu i dozadu) pro nulovou prodlevu při listování.

---

### Fáze 2: Zoom, lupa, správa souborů a mazání
* **Zoom a ovládání ve Fullscreenu:**
  * **Výchozí zobrazení:** *Fit to Window* (zmenšení velkých obrázků na velikost obrazovky se zachováním poměru stran; menší obrázky v 1:1 nebo dle nastavení).
  * **FastStone Lupa (100 % náhled):** Stisk a držení **levého tlačítka myši** (ve výchozím zobrazení) okamžitě přepne obrázek na 100 % (1:1) v místě kurzoru a tažením myši umožňuje plynulý posun (pan). Po uvolnění tlačítka se obraz vrátí zpět na *Fit to Window*.
  * **Kolečko myši (přepínatelné, výchozí = listování fotkami):**
    * Ve výchozím režimu kolečko myši plynule listuje mezi předchozím a dalším snímkem (stejně jako ve FastStone).
    * Podržení `Ctrl + kolečko myši` vždy provádí plynulý zoom přímo na pozici kurzoru.
    * Klávesou `M` (nebo tlačítkem v horní liště Overview) lze chování kolečka trvale přepnout mezi režimem **Listování fotkami** a **Zoom na kurzor**.
  * **Plynulý zoom:** Klávesy `+` / `-` (a `Ctrl + kolečko`). Při přiblížení lze obraz posouvat tažením myší.
  * **Rychlé měřítko:** Klávesa `*` (na numerické klávesnici i horní řadě) pro okamžitý přepínač / návrat na **100 % (1:1)** velikost (opětovný stisk nebo klávesa `/` vrátí na *Fit to Window*).
* **Kopírování souboru (`C`):**
  * Stisk klávesy `C` ve Fullscreenu (i v Overview) vyvolá lehký modální dialog s předvyplněnou poslední použitou cílovou složkou (s možností změny přes Procházet / psaním cesty) a okamžitým potvrzením klávesou `Enter`.
* **Mazání souboru (`Delete`):**
  * Rychlé a tiché smazání aktuálního souboru **přímo do Koše Windows** (Recycle Bin) klávesou `Delete` (bez zdržujícího potvrzovacího dialogu) a okamžitý posun na další obrázek ve složce.

---

### Fáze 3: Transformace a výstřižky (FastStone styl)
* **Transformace (s fyzickým zápisem do souboru):**
  * Klávesové zkratky ve Fullscreenu: `L` (rotace o 90° doleva), `R` (rotace o 90° doprava), `H` (horizontální zrcadlení), `V` (vertikální zrcadlení).
  * Změna se ihned projeví na obrazovce a rovnou se **zapíše do původního souboru** bez vytváření záložních kopií (u JPG s vysokou kvalitou 95 %+ nebo bezeztrátově, u PNG/WEBP bezeztrátově).
* **Výstřižek / Crop (`X`):**
  * Aktivace klávesou `X` ve Fullscreenu.
  * Interaktivní výběr obdélníkové oblasti tažením myši (s možností ztmavení okolí mimo výřez a zobrazením rozměrů výřezu v px). `Esc` režim výstřižku zruší.
  * Po výběru oblasti (nebo potvrzení `Enter`) následuje rychlý modální dialog s volbou:
    1. **Uložit do nového souboru** (např. s automatickým sufixem `_crop` nebo výběrem cesty).
    2. **Zkopírovat do schránky (Clipboard)** pro okamžité `Ctrl+V` v jiné aplikaci.

---

### Fáze 4: Dávková konverze (Batch Convert)
* **Rozhraní:** Přístupné z mřížkového přehledu (**Overview**), např. klávesou `F3` nebo tlačítkem/zkratkou `B`, případně přetažením (Drag & Drop) více souborů.
* **Funkcionalita:**
  * Seznam vstupních souborů (možnost přidat vybrané z Overview nebo přes Drag & Drop).
  * Nastavení výstupní složky a formátu (`JPG`, `PNG`, `WEBP`).
  * Nastavení kvality / komprese (posuvník 1–100 %).
  * Volitelná změna rozměrů (Resize – podle delší strany, šířky/výšky nebo procent se zachováním poměru stran).
  * Vícevláknové zpracování s ukazatelem průběhu (Progress Bar) a možností přerušení.
