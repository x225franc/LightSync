# Yeelight : cadence LAN et mode musique

Les couleurs du standalone et du plugin Toolkit sont envoyées uniquement après établissement de MusicLink (connexion retour TCP set_music). Le plugin OpenRGB suit le même principe. Le quota normal de 60 commandes/minute par connexion, 144 au total par appareil et quatre connexions ne constitue pas un plafond du flux musique. Aucun fallback rapide sur la connexion TCP normale n'est utilisé pour les couleurs.

La restriction artificielle de 54 commandes/minute a été retirée du flux musique. Cadence configurable : 1 à 20 FPS ; défaut 10 FPS. C'est un compromis logiciel ajustable, pas une cadence matérielle continue certifiée. Les réglages existants sont conservés par le code ; sur le PC de déploiement, l'ancien réglage 1/2 FPS est sauvegardé puis porté à 10 FPS. On peut redescendre manuellement en cas d'instabilité Wi-Fi ou de firmware.

Les explications Yeelight relatives aux 50 ms concernent la gestion des changements de couleur ; elles ne garantissent pas 20 FPS permanents. Les 30 ms de certaines transitions smooth ne sont pas non plus une garantie de FPS. L'interpolation reste optionnelle et ajoute un lissage ; elle peut être désactivée pour comparer une réponse directe. Le fade d'extinction est conservé.

Le standalone et Toolkit conservent le ralentissement adaptatif après déconnexion, la temporisation de reconnexion, les envois uniquement sur changement et les keepalives. OpenRGB conserve sa limitation des données en attente et abandonne les nouveaux envois lorsque le transport est encombré. Les chemins de libération set_music [0] à l'arrêt restent inchangés. Le contrôle normal ne reçoit que les commandes d'établissement/libération ou d'alimentation occasionnelles ; cette correction n'y applique aucun flux rapide.

Validation : test moteur pour chaque variante C# : 80 changements simulés en moins d'une minute passent le seuil ancien de 54 sans pause, avec contrôle de cadence. Tests de reconnexion, gel écran de veille, extinction et libération acquittée conservés. Les simulations ne prouvent pas une limite matérielle. Pour vérifier les ampoules réelles, utiliser une seule application à la fois, commencer à 10 FPS, observer une séquence de couleurs pendant plusieurs minutes et rechercher les reconnexions dans les logs. Comparer ensuite 5, 10 et 20 FPS si nécessaire. Une mesure de latence lumineuse requiert une caméra ou un capteur ; aucun résultat de ce type n'est annoncé ici.

Sources :
- https://www.yeelight.com/download/Yeelight_Inter-Operation_Spec.pdf
- https://home.yeelight.de/site/templates/downloads/yeelight_inter-operation-spec.pdf
- https://forum.yeelight.com/t/set-music-command/73
- https://www.home-assistant.io/integrations/yeelight/