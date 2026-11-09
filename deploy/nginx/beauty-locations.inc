# Спільні location-блоки (include всередині server { }) для хостового nginx (beauty.conf) і контейнерного proxy.
# Передбачає upstream-и `beauty_web` (Next.js) і `beauty_api` (Kestrel), визначені в http-контексті.
# Well-known файли беруться з /var/www/beauty-wellknown/ (змініть alias, якщо кладете деінде).

# --- Параметри проксування (наслідуються всіма location нижче) ---
proxy_http_version 1.1;
# $http_host (з портом, якщо клієнт його надіслав): BFF Next порівнює Origin із Host/X-Forwarded-Host (CSRF-захист входу);
# $host відрізає нестандартний зовнішній порт і дав би 403 forbidden_origin.
proxy_set_header Host $http_host;
proxy_set_header X-Forwarded-Host $http_host;
proxy_set_header X-Forwarded-Proto $scheme;
# ПЕРЕЗАПИСУЄМО (а не дописуємо $proxy_add_x_forwarded_for): nginx тут крайній проксі, клієнтський X-Forwarded-For
# недовірений. Якщо перед nginx є ще один проксі/CDN, потрібен real_ip-модуль і окреме рішення.
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Real-IP $remote_addr;
proxy_read_timeout 60s;

# --- Universal Links / App Links: без редиректів, application/json ---
location = /.well-known/apple-app-site-association {
    alias /var/www/beauty-wellknown/apple-app-site-association;
    default_type application/json;
    add_header Cache-Control "public, max-age=300" always;
}
location = /.well-known/assetlinks.json {
    alias /var/www/beauty-wellknown/assetlinks.json;
    default_type application/json;
    add_header Cache-Control "public, max-age=300" always;
}

# --- API оператора платформи (створення tenant, X-Platform-Key) назовні НЕ віддаємо ---
location ^~ /api/platform/ {
    return 404;
}

# --- BFF Next.js (httpOnly refresh-cookie): це маршрути web, а не backend ---
location ^~ /api/session/ {
    proxy_pass http://beauty_web;
}

# --- Backend API (Kestrel): /api/auth, /api/beauty, /api/public, вебхуки каналів ---
location /api/ {
    proxy_pass http://beauty_api;
}

# --- Усе інше: Next.js ---
location / {
    proxy_pass http://beauty_web;
}
