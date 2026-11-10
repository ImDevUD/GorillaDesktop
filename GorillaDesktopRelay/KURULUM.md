# GorillaDesktop aktarma sunucusu: kurulum

Bu sunucu, ekranını paylaşan kişinin görüntüsünü ve sesini lobideki diğer modlu
oyunculara (en fazla 10 kişi) dağıtır. Hiçbir şey kaydetmez, sadece iletir.
Kimse kimsenin IP adresini görmez.

Bir kez kurulur, sonra hep aynı adres kullanılır. Bir arkadaş grubunda tek bir
kişinin kurması yeter.

## Anahtar

Kendine uzun, rastgele bir anahtar uydur (ör. 20+ harf ve rakam). Bu bir şifre gibidir:
sunucuya `SECRET` olarak, moda da `Anahtar` olarak aynısı girilir.
Kimseyle paylaşma; sadece birlikte izleyeceğin arkadaşlarının config dosyasına girecek.

## 1) Render'da sunucuyu çalıştır (5-10 dk)

1. https://render.com adresinde hesap aç.
2. **New +** > **Web Service**.
3. **Public Git Repository** sekmesine bu deponun adresini yapıştır ve **Connect**.
   (Depoyu kendi GitHub hesabına fork'ladıysan GitHub ile bağlayıp onu da seçebilirsin.)
4. Ayarlar:
   - **Root Directory:** `GorillaDesktopRelay`
   - **Language / Runtime:** `Docker`
   - **Instance Type:** `Free`
5. **Environment Variables** bölümüne ekle:
   - `SECRET` = anahtarın
   - `MAX_VIEWERS` = `10`
6. **Deploy Web Service**. İlk kurulum birkaç dakika sürer.
7. Bitince sayfanın üstünde bir adres çıkar, ör. `https://gorilladesktop-relay.onrender.com`.
   Tarayıcıda açınca `GorillaDesktop relay OK` yazıyorsa çalışıyor.

## 2) Moda adresi gir

Oyunu bir kez açıp kapat, sonra şu dosyayı Not Defteri ile aç:

`Gorilla Tag\BepInEx\config\com.gorilladesktop.gtag.cfg`

`[Paylasim]` bölümünü şöyle doldur (adresin başı **wss://** olacak):

```
Sunucu = wss://gorilladesktop-relay.onrender.com
Anahtar = ANAHTARIN
```

Birlikte izleyeceğin herkeste bu iki satır **aynı** olmalı.

## Kullanım

1. Hepiniz aynı **Utilla modlu lobiye** girin.
2. Yayın açacak kişi kendi ekranını açar (sol Y), ekranı istediği yere koyar (ör. Stump).
3. Ayarlar (⚙) > **Arkadaşlarla paylaş** > AÇIK.
4. Diğerleri ekranı aynı yerde görür; üstünde "X yayında" yazar, ses ekrandan gelir.
5. Yayıncı ekranı taşırsa herkeste taşınır.
6. Kapatmak için aynı ayarı KAPALI yap, ya da kendi ekranını kapat (sol Y).
   Lobiden çıkınca da ekran boşa düşer, başkası alabilir.

Ekranın hep aynı yerde durmasını istiyorsan: ekranı oraya koy ve
Ayarlar > **Ortak ekranın yeri burası olsun**. Kimse yayında değilken orada boş bir
ekran durur; dokunan alır. Konum config'deki `SabitKonum` satırına yazılır,
arkadaşlarında da aynı olmalı.

## Bilmen gerekenler

- **Uyku:** Ücretsiz Render sunucusu 15 dakika kullanılmazsa uyur.
  İlk bağlanışta uyanması yaklaşık 1 dakika sürer; mod kendisi tekrar dener.
- **İnternet kotası:** Video oynarken her izleyici yaklaşık 2-4 Mbps kullanır.
  10 kişiyle saatte 10-15 GB eder. Ücretsiz planın aylık bir veri sınırı var
  (Render panelinde görünür). Ekran durağansa neredeyse hiç veri gitmez.
- **Kalite:** Config'deki `Kalite` (0.3-0.9) ve `FPS` (5-30) ile ayarlanır.
- **Güvenlik:** İzleyenler sadece bakar, yayıncının bilgisayarına tıklayamaz.
