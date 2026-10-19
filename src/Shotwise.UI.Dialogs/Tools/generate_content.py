"""Generate original vector content and English dialog resource keys. Run from repo root.

RETIRED for stamps (2026-10-18): stamps are now produced by tools/stamp-gen.
Running this script would recreate the 7 obsolete v1 stamp folders - do not run its stamp section.
"""
from pathlib import Path
import json, re, math, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[3]
CONTENT = ROOT / 'src/Shotwise.App/Content'
LANGS = ['en','de','es','fr','ja','pt-BR']
CATEGORIES = {
'arrows': ['Arrows','Pfeile','Flechas','Flèches','矢印','Setas'],
'numbers': ['Numbers and badges','Zahlen und Abzeichen','Números e insignias','Chiffres et badges','数字とバッジ','Números e emblemas'],
'status': ['Checks and warnings','Häkchen und Warnungen','Marcas y advertencias','Coches et avertissements','チェックと警告','Marcas e avisos'],
'ui': ['Interface icons','Oberflächensymbole','Iconos de interfaz','Icônes d’interface','UIアイコン','Ícones de interface'],
'shapes': ['Shapes','Formen','Formas','Formes','図形','Formas'],
'labels': ['Callout labels','Beschriftungen','Etiquetas','Étiquettes','ラベル','Rótulos'],
'emoji': ['Expressions','Ausdrücke','Expresiones','Expressions','表情','Expressões']}
COLORS = ['#2563eb','#0f766e','#7c3aed','#be123c','#b45309','#334155']
def svg(body): return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">' + body + '</svg>\n'
def polygon(points, fill, stroke='#172033'):
    return f'<polygon points="{points}" fill="{fill}" stroke="{stroke}" stroke-width="4" stroke-linejoin="round"/>'
def text(value, y=78, size=44, color='white'):
    return f'<text x="64" y="{y}" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="{size}" font-weight="700" fill="{color}">{value}</text>'
UI_NAMES = ['window','folder','document','save','search','camera','video','microphone','speaker','settings','lock','unlock','mail','calendar','clock','cloud','upload','download']
UI_PATHS = [
'<rect x="20" y="28" width="88" height="72" rx="7"/><path d="M20 47h88M32 37h3m8 0h3"/>',
'<path d="M16 38V28h37l12 12h47v63H16zM16 52h96"/>',
'<path d="M33 16h42l22 22v74H33zM75 16v24h22M45 59h40M45 74h40M45 89h24"/>',
'<path d="M22 20h75l12 12v76H22zM39 20v31h48V20M39 108V72h49v36"/>',
'<circle cx="55" cy="54" r="30"/><path d="m77 76 31 31"/>',
'<path d="M17 39h24l9-13h29l9 13h23v62H17z"/><circle cx="64" cy="70" r="22"/>',
'<rect x="17" y="32" width="64" height="65" rx="8"/><path d="m81 53 29-17v57L81 77z"/>',
'<rect x="49" y="14" width="30" height="62" rx="15"/><path d="M34 58v9a30 30 0 0 0 60 0v-9M64 97v17M45 114h38"/>',
'<path d="M17 49h23l28-24v79L40 80H17zM83 43q24 21 0 42M96 30q36 34 0 68"/>',
'<circle cx="64" cy="64" r="32"/><circle cx="64" cy="64" r="12"/><path d="M64 16v15m0 66v15M16 64h15m66 0h15M30 30l11 11m46 46 11 11M30 98l11-11m46-46 11-11"/>',
'<rect x="29" y="55" width="70" height="54" rx="8"/><path d="M42 55V38a22 22 0 0 1 44 0v17M64 76v14"/>',
'<rect x="29" y="55" width="70" height="54" rx="8"/><path d="M42 55V38a22 22 0 0 1 44 0M64 76v14"/>',
'<rect x="17" y="30" width="94" height="72" rx="6"/><path d="m17 36 47 38 47-38M17 102l33-29m61 29L78 73"/>',
'<rect x="21" y="27" width="86" height="82" rx="6"/><path d="M21 47h86M43 17v22m42-22v22M38 64h12m14 0h12m14 0h3M38 82h12m14 0h12m14 0h3"/>',
'<circle cx="64" cy="64" r="45"/><path d="M64 32v32l23 16"/>',
'<path d="M33 96a23 23 0 0 1-1-46 33 33 0 0 1 64-7 27 27 0 0 1 0 53z"/>',
'<path d="M23 83v25h82V83M64 85V20m-25 25 25-25 25 25"/>',
'<path d="M23 83v25h82V83M64 20v65M39 60l25 25 25-25"/>' ]
for category, names in CATEGORIES.items():
    folder=CONTENT/'stamps'/category; folder.mkdir(parents=True,exist_ok=True); stamps=[]
    for i in range(18):
        color=COLORS[i%6]; name=f'{category}-{i+1:02d}'
        if category=='arrows':
            angle=(i%8)*45; bend=i//8
            if bend==0: body='<path d="M15 51h61V30l37 34-37 34V77H15z"/>'
            elif bend==1: body='<path d="M20 104V49h52V25l39 36-39 36V74H45v30z"/>'
            else: body='<path d="M17 47h60V25l35 39-35 39V81H17zM17 54V74"/>'
            body=f'<g transform="rotate({angle} 64 64)" fill="{color}" stroke="#172033" stroke-width="3" stroke-linejoin="round">{body}</g>'
        elif category=='numbers':
            shape=f'<circle cx="64" cy="64" r="49" fill="{color}"/>' if i%2==0 else f'<rect x="15" y="15" width="98" height="98" rx="20" fill="{color}"/>'
            body=shape+text(str(i+1))
        elif category=='status':
            kind=i%3
            base=f'<circle cx="64" cy="64" r="49" fill="{color}"/>' if i<9 else polygon('64,10 119,109 9,109',color)
            mark=['<path d="m34 66 20 20 42-45"/>','<path d="m42 42 44 44m0-44L42 86"/>','<path d="M64 36v36m0 13v6"/>'][kind]
            body=base+'<g fill="none" stroke="white" stroke-width="10" stroke-linecap="round" stroke-linejoin="round">'+mark+'</g>'
        elif category=='ui':
            name=UI_NAMES[i]; body=f'<g fill="none" stroke="{color}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round">{UI_PATHS[i]}</g>'
        elif category=='shapes':
            n=3+i%7; pts=' '.join(f'{64+51*math.cos(-math.pi/2+j*2*math.pi/n):.2f},{64+51*math.sin(-math.pi/2+j*2*math.pi/n):.2f}' for j in range(n))
            body=polygon(pts,color if i<9 else 'none')
        elif category=='labels':
            labels=['NOTE','TIP','START','END','YES','NO','NEW','IDEA','LOOK','HERE','STEP','WHY','HOW','CHECK','FIX','READY','WAIT','DONE']
            name=labels[i].lower(); body=f'<path d="M12 20h104v68H66l-26 26V88H12z" fill="{color}" stroke="#172033" stroke-width="3"/>'+text(labels[i],64,24)
        else:
            eyeY=46+i%3*4; mouth=76+i%4*3
            face=f'<circle cx="64" cy="64" r="51" fill="{["#fde68a","#fdba74","#bef264"][i//6]}" stroke="#78350f" stroke-width="3"/>'
            eyes=f'<circle cx="45" cy="{eyeY}" r="5"/><circle cx="83" cy="{eyeY}" r="5"/>' if i%3!=2 else f'<path d="m36 {eyeY} 9-6 9 6m20 0 9-6 9 6" fill="none" stroke="black" stroke-width="4"/>'
            mouths=[f'<path d="M37 {mouth}q27 32 54 0" fill="none" stroke="black" stroke-width="5"/>',f'<ellipse cx="64" cy="{mouth+6}" rx="12" ry="16"/>',f'<path d="M39 {mouth}h50" stroke="black" stroke-width="5"/>',f'<path d="M37 {mouth+15}q27-30 54 0" fill="none" stroke="black" stroke-width="5"/>'][i%4]
            body=face+eyes+mouths
        filename=name+'.svg'; (folder/filename).write_text(svg(body),encoding='utf-8')
        stamps.append({'file':filename,'name':name,'keywords':{lang:[names[k],name,str(i+1)] for k,lang in enumerate(LANGS)}})
    package={'id':'shotwise.'+category,'version':1,'name':dict(zip(LANGS,names)),'category':dict(zip(LANGS,names)),'stamps':stamps}
    (folder/'packageinfo.json').write_text(json.dumps(package,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
cursors={
'arrow':'<path d="M29 12v94l24-25 19 34 17-10-20-32 35-5z" fill="white" stroke="#111827" stroke-width="5"/>',
'hand':'<path d="M40 70V23q0-14 13-12 9 1 9 13v34-19q0-13 13-8 7 3 7 12v18-12q0-13 13-7 7 3 7 11v35q0 28-26 28H57q-14 0-23-15L18 77q-6-15 8-15 7 0 14 8z" fill="white" stroke="#111827" stroke-width="5" stroke-linejoin="round"/>',
'ibeam':'<path d="M42 12h44M64 12v104m-22 0h44" stroke="white" stroke-width="10"/><path d="M42 12h44M64 12v104m-22 0h44" stroke="#111827" stroke-width="4"/>',
'wait':'<path d="M33 15h62M33 113h62M38 15q0 35 26 49-26 14-26 49m52-98q0 35-26 49 26 14 26 49" fill="#dbeafe" stroke="#111827" stroke-width="5"/>',
'cross':'<path d="M64 10v108M10 64h108" stroke="white" stroke-width="10"/><path d="M64 10v108M10 64h108" stroke="#111827" stroke-width="4"/>',
'move':'<path d="M64 8 48 28h10v30H28V48L8 64l20 16V70h30v30H48l16 20 16-20H70V70h30v10l20-16-20-16v10H70V28h10z" fill="white" stroke="#111827" stroke-width="4"/>'}
folder=CONTENT/'cursors'; folder.mkdir(parents=True,exist_ok=True)
for name,body in cursors.items(): (folder/(name+'.svg')).write_text(svg(body),encoding='utf-8')
keys=set()
for file in (ROOT/'src/Shotwise.UI.Dialogs').glob('*.cs'):
    source=file.read_text(encoding='utf-8-sig')
    keys.update(re.findall(r'(?:Get|FormWindow|Text|Check|Choice|Enum|Action|Field)\("([A-Za-z][A-Za-z0-9]+)"',source))
for file in [ROOT/'src/Shotwise.Core/Settings/AppSettings.cs',ROOT/'src/Shotwise.Core/Imaging/ImagingContracts.cs', ROOT/'src/Shotwise.Core/Effects/Effects.cs']:
    keys.update(re.findall(r'\b(?:public\s+)?[\w<>?]+\s+(\w+)\s*\{\s*get;',file.read_text(encoding='utf-8-sig')))
messages={'OK':'OK','Dpi':'DPI','Fps':'Frames per second','HexColor':'HEX (#RRGGBB or #AARRGGBB)','LongGifWarning':'This GIF exceeds 30 seconds and may create a large file.','ImageTooLarge':'Choose positive dimensions below 512 megapixels.','WholeNumberRequired':'Enter a whole number.','PathRequired':'Choose an output file.','FolderMissing':'The output folder does not exist.','FilesMissing':'Choose existing input files.','InvalidEncoding':'Quality must be 1–100; choose a supported bit depth.','InvalidCounter':'Counter digits must be 1–12 and the start must be nonnegative.','InvalidTrayCount':'The tray must contain 1–200 items.','AgreementRequired':'Accept the experimental feature terms before enabling experiments.','CapturePrinterRefused':'Choose a different printer. Printing to the capture printer would create a capture loop.','InvalidPageSetup':'Choose valid margins, copies, spacing, scale, and grid dimensions.','MarginsTooLarge':'The margins and spacing leave no room for an image.','LicensesMissing':'Third-party licence notices were not included in this installation.','ResetStylesWarning':'Restore the built-in styles?','ResetSettingsWarning':'Reset all preferences to the Shotwise defaults?','FolderLoaderMissing':'This destination has no remote folder provider.','TwoImagesRequired':'Choose at least two images.','TwoVideosRequired':'Choose at least two videos.'}
# Include record-constructor parameter and enum labels as localizable keys.
for file in [ROOT/'src/Shotwise.Core/Settings/AppSettings.cs', ROOT/'src/Shotwise.Core/Imaging/ImagingContracts.cs', ROOT/'src/Shotwise.Core/Effects/Effects.cs']:
    source = file.read_text(encoding='utf-8-sig')
    keys.update(re.findall(r'\b(?:int|double|bool|string|Argb|Anchor9|PaperSize|PageOrientation|PageImageLayout|JpegSubsampling|Corner|EdgeStyle|EdgeSides|FilterKind|ShadowDirection|CaptionFields|Rect2|Thickness2|PdfPageOptions)\??\s+([A-Z]\w*)',source))
    for enum in re.findall(r'enum \w+\s*\{([^}]+)\}',source,re.S):
        keys.update(re.findall(r'(?:^|,)\s*([A-Za-z]\w*)',enum))
keys.update(messages)
root=ET.Element('root')
for name,value in [('resmimetype','text/microsoft-resx'),('version','2.0'),('reader','System.Resources.ResXResourceReader, System.Windows.Forms'),('writer','System.Resources.ResXResourceWriter, System.Windows.Forms')]:
    node=ET.SubElement(root,'resheader',name=name); ET.SubElement(node,'value').text=value
for key in sorted(keys):
    node=ET.SubElement(root,'data',name=key); node.set('xml:space','preserve'); ET.SubElement(node,'value').text=messages.get(key,re.sub(r'(?<=[a-z])([A-Z])',r' \1',key))
ET.indent(root); ET.ElementTree(root).write(ROOT/'src/Shotwise.UI.Dialogs/Resources/DialogStrings.resx',encoding='utf-8',xml_declaration=True)
print('Generated 126 original stamps, 7 manifests, 6 cursors and dialog resources.')

# Translated common dialog chrome; other entries use the English resource fallback.
translations = {
'de': {'OK':'OK','Cancel':'Abbrechen','Browse':'Durchsuchen','Copy':'Kopieren','CopyAll':'Alles kopieren','SaveAs':'Speichern unter','Preferences':'Einstellungen','Width':'Breite','Height':'Höhe','Background':'Hintergrund','Format':'Format','Preview':'Vorschau','MoveUp':'Nach oben','MoveDown':'Nach unten','Required':'Erforderlich','NewImage':'Neues Bild','ResizeImage':'Bildgröße ändern','ColorPicker':'Farbauswahl','Print':'Drucken'},
'es': {'OK':'Aceptar','Cancel':'Cancelar','Browse':'Examinar','Copy':'Copiar','CopyAll':'Copiar todo','SaveAs':'Guardar como','Preferences':'Preferencias','Width':'Ancho','Height':'Alto','Background':'Fondo','Format':'Formato','Preview':'Vista previa','MoveUp':'Subir','MoveDown':'Bajar','Required':'Obligatorio','NewImage':'Nueva imagen','ResizeImage':'Cambiar tamaño de imagen','ColorPicker':'Selector de color','Print':'Imprimir'},
'fr': {'OK':'OK','Cancel':'Annuler','Browse':'Parcourir','Copy':'Copier','CopyAll':'Tout copier','SaveAs':'Enregistrer sous','Preferences':'Préférences','Width':'Largeur','Height':'Hauteur','Background':'Arrière-plan','Format':'Format','Preview':'Aperçu','MoveUp':'Monter','MoveDown':'Descendre','Required':'Obligatoire','NewImage':'Nouvelle image','ResizeImage':'Redimensionner l’image','ColorPicker':'Sélecteur de couleur','Print':'Imprimer'},
'ja': {'OK':'OK','Cancel':'キャンセル','Browse':'参照','Copy':'コピー','CopyAll':'すべてコピー','SaveAs':'名前を付けて保存','Preferences':'設定','Width':'幅','Height':'高さ','Background':'背景','Format':'形式','Preview':'プレビュー','MoveUp':'上へ','MoveDown':'下へ','Required':'必須','NewImage':'新しい画像','ResizeImage':'画像サイズを変更','ColorPicker':'色の選択','Print':'印刷'},
'pt-BR': {'OK':'OK','Cancel':'Cancelar','Browse':'Procurar','Copy':'Copiar','CopyAll':'Copiar tudo','SaveAs':'Salvar como','Preferences':'Preferências','Width':'Largura','Height':'Altura','Background':'Fundo','Format':'Formato','Preview':'Visualização','MoveUp':'Mover para cima','MoveDown':'Mover para baixo','Required':'Obrigatório','NewImage':'Nova imagem','ResizeImage':'Redimensionar imagem','ColorPicker':'Seletor de cores','Print':'Imprimir'}}
for culture, entries in translations.items():
    satellite=ET.Element('root')
    for header in root.findall('resheader'):
        import copy
        satellite.append(copy.deepcopy(header))
    for key,value in entries.items():
        item=ET.SubElement(satellite,'data',name=key); item.set('xml:space','preserve'); ET.SubElement(item,'value').text=value
    ET.indent(satellite); ET.ElementTree(satellite).write(ROOT/f'src/Shotwise.UI.Dialogs/Resources/DialogStrings.{culture}.resx',encoding='utf-8',xml_declaration=True)
