#!/usr/bin/env python3
"""
Static checks for the WPF code, which cannot be compiled where this was written (it needs Windows). Run from the repository root:  python3 Tests/verify_static.py
Catches the mistakes that were actually made along the way: a XAML file that doesn't parse; an event handler named in XAML with no method behind it; a {Binding} with no such property; a
{StaticResource}/{DynamicResource} key defined nowhere; a trigger whose TargetName names an element missing from its template; the same implicit style defined twice (the nearer one silently wins);
a WPF type used in a file that doesn't import its namespace; unbalanced brackets. Prints OK or the problems; exit code 1 if there are any.
"""
import glob, re, sys, xml.etree.ElementTree as ET

NS = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
XK = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'
XN = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
APP = 'CleanPrint.App'
problems = []
def bad(msg): problems.append(msg)
def rd(p): return open(p, encoding='utf-8').read()
def files(pattern): return [f for f in glob.glob(pattern, recursive=True) if '/obj/' not in f and '/bin/' not in f]

xamls = files(f'{APP}/**/*.xaml')
codes = files(f'{APP}/**/*.cs') + files('CleanPrint.Core/**/*.cs') + files('CleanPrint.Watcher/**/*.cs')

# 1. every XAML file parses
trees = {}
for f in xamls:
    try: trees[f] = ET.parse(f).getroot()
    except ET.ParseError as e: bad(f'XAML does not parse: {f}: {e}')

# 2. handlers named in XAML exist in the code-behind
for f in xamls:
    cs = f + '.cs'
    try: code = rd(cs)
    except FileNotFoundError: continue
    # only attributes that can be events (an x:Name like PART_TextBox looks the same but is not a handler)
    for h in sorted(set(m.group(2) for m in re.finditer(r'(?<=\s)(?!Name=|Key=|Style=|Text=|Content=|Header=|Tag=|ToolTip=|Data=|Source=|Template=)([A-Za-z]\w*)="((?:[A-Z]\w*)_[A-Z]\w+)"', rd(f)))):
        if not re.search(r'\b' + h + r'\s*\(', code): bad(f'{f}: handler {h} has no method in {cs}')

# 3. bindings name a property that exists somewhere (lenient: any public member of any App/Core class)
members = set()
for f in codes:
    t = rd(f)
    members |= set(re.findall(r'public\s+(?:static\s+|override\s+|virtual\s+|required\s+)*[\w<>\[\]?,\.\(\) ]+?\s+(\w+)\s*(?:\{|=>|=|;|\()', t))
    members |= set(re.findall(r'public\s+(?:readonly\s+)?(?:\w+<[^>]+>|\w+)\s+(\w+)\s*\{', t))
skip = {'ActualWidth', 'ActualHeight', 'IsDropDownOpen', 'HorizontalOffset', 'VerticalOffset', 'Foreground', 'Name', 'X', 'Y', 'StringFormat', 'Path', 'Mode', 'Source', 'Foreground', 'DataContext', 'ElementName', 'RelativeSource', 'Converter', 'FallbackValue', 'IsChecked'}
for f in xamls:
    if '/Themes/' in f: continue
    for b in sorted(set(re.findall(r'\{Binding (?:Path=)?(?:DataContext\.)?(\w+)', rd(f)))):
        if b not in members and b not in skip: bad(f'{f}: {{Binding {b}}} - no such public member anywhere')

# 4. resource keys used are defined (in XAML x:Key, or written by ThemeManager)
defined = set()
for root in trees.values():
    for el in root.iter():
        k = el.get(XK)
        if k: defined.add(k)
tm = rd(f'{APP}/Theming/ThemeManager.cs') if glob.glob(f'{APP}/Theming/ThemeManager.cs') else ''
defined |= set(re.findall(r'Set\("(\w+)"', tm)) | set(re.findall(r'r\["(\w+)"\]', tm)) | set(re.findall(r'Resources\["(\w+)"\]', tm))
for f in xamls:
    for k in sorted(set(re.findall(r'\{(?:Dynamic|Static)Resource (\w+)\}', rd(f)))):
        if k not in defined and not k.startswith(('System', 'x:')): bad(f'{f}: resource key {k} is defined nowhere')

# 5. TargetName in a template's triggers names an element of that template
for f, root in trees.items():
    for tpl in root.iter(NS + 'ControlTemplate'):
        names = {e.get(XN) for e in tpl.iter() if e.get(XN)}
        for e in tpl.iter():
            tn = e.get('TargetName')
            if tn and tn not in names: bad(f'{f}: a {tpl.get("TargetType")} template refers to TargetName={tn}, which is not in it')

# 6. no implicit style (a Style with TargetType and no x:Key) defined twice, except scoped overrides that say BasedOn
seen = {}
for f, root in trees.items():
    for el in root.iter(NS + 'Style'):
        tt, key = el.get('TargetType'), el.get(XK)
        if tt and not key and not el.get('BasedOn'): seen.setdefault(tt, set()).add(f)
for tt, fs in seen.items():
    if len(fs) > 1: bad(f'implicit style for {tt} is defined in more than one file: {sorted(fs)}')

# 7. WPF types used without their namespace imported (or the type qualified)
wpf = {'Thickness': 'System.Windows', 'CornerRadius': 'System.Windows', 'Visibility': 'System.Windows', 'Application': 'System.Windows',
       'DoubleCollection': 'System.Windows.Media', 'SolidColorBrush': 'System.Windows.Media', 'Colors': 'System.Windows.Media'}
for f in files(f'{APP}/**/*.cs'):
    t = rd(f)
    code = re.sub(r'//[^\n]*', '', re.sub(r'"(?:\\.|[^"\\])*"', '""', t))
    for typ, ns in wpf.items():
        if re.search(r'(?<![\w\.])' + typ + r'\b\s*[\w\(<\?]', code) and f'using {ns};' not in t and f'{ns}.{typ}' not in t and f'namespace {ns}' not in t:
            # a type of this name could be the file's own member; only report if it is not declared/defined here
            if not re.search(r'(class|enum|struct|record)\s+' + typ + r'\b', t): bad(f'{f}: uses {typ} but does not import {ns}')

# 8. brackets balance in every C# file (strings and comments removed first)
def strip(t):
    t = re.sub(r'\$?@?"(?:\\.|[^"\\])*"', '""', t)           # strings first, so a // inside a string can't start a "comment"
    t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
    return re.sub(r'//[^\n]*', '', t)
for f in codes:
    t = strip(rd(f))
    for a, b in ('{}', '()', '[]'):
        if t.count(a) != t.count(b): bad(f'{f}: unbalanced {a}{b} ({t.count(a)} vs {t.count(b)})')

# 9. a property with a private/protected setter is assigned only inside its own file (anything else is a compile error, CS0272)
restricted = {}
for f in files(f'{APP}/ViewModels/*.cs'):   # the view models are what other code reaches into (a dialog's own private-set property can share a name with a .NET one)
    for m in re.finditer(r'public\s+[\w<>\[\]?,\. ]+?\s+(\w+)\s*\{[^{}]*?(?:private|protected)\s+(?:set|init)', rd(f)):
        restricted.setdefault(m.group(1), set()).add(f)
for name, owners in restricted.items():
    for f in files(f'{APP}/**/*.cs') + files('CleanPrint.Watcher/**/*.cs'):
        if f in owners: continue
        for m in re.finditer(r'\.' + name + r'\s*(?:=|\+=|-=)[^=]', strip(rd(f))):
            bad(f'{f}: assigns .{name}, whose setter is private/protected in {sorted(owners)}')

print(f'{len(xamls)} XAML files, {len(codes)} C# files checked.')
if problems:
    print(f'{len(problems)} PROBLEM(S):')
    for p in problems: print('  -', p)
    sys.exit(1)
print('OK: no problems found.')
