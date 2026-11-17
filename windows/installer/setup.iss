; Instalador de Fábrica de Apuntes para Windows (por usuario, sin permisos de administrador)
#define Nombre "Fábrica de Apuntes"
#define Version "1.0.0"

[Setup]
AppId={{6F2C1B7A-3D54-4E1B-9A0E-FA0B1CA5D001}
AppName={#Nombre}
AppVersion={#Version}
AppPublisher=Fábrica de Apuntes
DefaultDirName={localappdata}\Programs\{#Nombre}
DefaultGroupName={#Nombre}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\salida
OutputBaseFilename=Fabrica-de-Apuntes-Setup
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\FabricaDeApuntes.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Files]
Source: "..\publicar\FabricaDeApuntes.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\recursos\*"; DestDir: "{app}\recursos"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#Nombre}"; Filename: "{app}\FabricaDeApuntes.exe"
Name: "{autodesktop}\{#Nombre}"; Filename: "{app}\FabricaDeApuntes.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FabricaDeApuntes.exe"; Description: "Abrir {#Nombre}"; Flags: nowait postinstall skipifsilent
