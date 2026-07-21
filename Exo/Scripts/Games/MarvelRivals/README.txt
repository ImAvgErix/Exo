Marvel Rivals seeds (shipped with Exo)
=====================================

On Apply, Exo copies these into the game install if missing:

  bypass\
    dsound.dll
    MarvelRivalsUTOCSignatureBypass.asi
      → MarvelGame\Marvel\Binaries\Win64\ (+ plugins\)

  packs\
    Exo* / zRigs* / zEvolve* .pak/.ucas/.utoc
      → MarvelGame\Marvel\Content\Paks\~mods\

Also always writes:
  %LocalAppData%\Marvel\Saved\Config\Windows\Engine.ini
  Scalability.ini + GameUserSettings scalability pins

Clean PC with only the Steam game installed:
  1) Create ~mods if missing
  2) Install bypass from this folder
  3) Install packs for Potato or Optimized
  4) Write configs

If bypass write fails (game running / permissions), configs still apply;
restart Exo as admin with the game closed and Reapply.
