# GorillaDesktop

Gorilla Tag için bilgisayar ekranını oyunun içine getiren BepInEx modu.
Ekranını VR'da önünde görürsün, parmağınla ya da lazerle kullanırsın, PC sesi de
ekrandan gelir. İstersen modlu lobide arkadaşlarınla birlikte izleyebilirsin.

## Özellikler

- **Oyun içi monitör:** Ana monitörün tam çözünürlükte, ekran kartı üzerinden yakalanır
  (Desktop Duplication). Oyunu kasmaz.
- **GT düğmesi gibi dokunma:** İşaret parmağının ucuyla ekrana basarak tıklarsın;
  titreşim ve tık sesi gelir.
- **Uzaktan lazer** (isteğe bağlı): İşaret parmağının devamından çıkar, tetikle tıklar.
- **İki elle büyütme:** Ekranı grip ile tut; iki elle tutup açınca büyür, kapatınca küçülür.
- **PC sesi oyunda:** Ses ekrandan 3B olarak gelir.
- **Ayarlar paneli:** Ekranın sağındaki ⚙ tuşu. Monitör, FPS, ses, dokunma, lazer, logo.
- **Arkadaşlarla izleme:** Utilla modlu lobide ekranını herkese yayınla (en fazla 10 izleyici).
- WalkSim ile masaüstünden de çalışır (F8 aç/kapat, F9 tık).

## Kurulum

1. Gorilla Tag'de **BepInEx** kurulu olmalı. Arkadaşlarla izleme için **Utilla** da gerekir.
2. [Releases](../../releases) sayfasından son sürümün zip'ini indir.
3. İçindeki `GorillaDesktop.dll` dosyasını `Gorilla Tag\BepInEx\plugins` klasörüne koy.
4. Oyunu aç, **sol kumandadaki Y** tuşuna bas.

## Kontroller (VR)

| Tuş | İşlev |
|---|---|
| Sol **Y** | Ekranı aç/kapat |
| İşaret parmağıyla ekrana dokun | Tıkla |
| **Grip** (el ekranın yanındayken) | Ekranı tut ve taşı; iki elle tutup büyüt/küçült |
| **Tetik** (lazer açıkken) | Lazerle tıkla |
| **A / X** | Sağ tık |
| **Joystick** | Kaydır |
| Sağ **B** | Windows ekran klavyesi |

## PC sesi

Mod, PC sesini sanal bir ses cihazından (varsayılan: **Steam Streaming Speakers**) yakalar
ve oyunun kendi sesini kulaklıkta tutar. Ekran açıkken bu yönlendirmeyi otomatik yapar,
kapatınca eski haline döndürür. Config'den kapatılabilir.

## Arkadaşlarla izleme

Bir aktarma sunucusu gerekir; bir arkadaş grubunda tek kişinin kurması yeter.
Adımlar: [GorillaDesktopRelay/KURULUM.md](GorillaDesktopRelay/KURULUM.md)

## Ayarlar

Oyunu bir kez açınca `Gorilla Tag\BepInEx\config\com.gorilladesktop.gtag.cfg` oluşur.
Her ayarın açıklaması dosyanın içinde yazıyor.

## Kaynaktan derleme

.NET SDK gerekir. `GorillaDesktop/GorillaDesktop.csproj` içindeki `GamePath` oyunun
kurulu olduğu klasörü göstermeli (ya da `-p:GamePath="..."` ile ver).

```
dotnet build GorillaDesktop -c Release
```

Derlenen DLL otomatik olarak oyunun `BepInEx\plugins` klasörüne kopyalanır.
