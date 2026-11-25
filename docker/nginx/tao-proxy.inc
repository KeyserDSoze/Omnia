proxy_http_version 1.1;
proxy_set_header Connection "";
# $http_host keeps a non-default port (e.g. localhost:59800) for Keycloak back-channel URLs.
proxy_set_header Host $http_host;
# Overwrite (never append) forwarding headers so clients cannot inject them.
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto $tao_forwarded_proto;
proxy_set_header X-Forwarded-Host $http_host;
proxy_set_header X-Forwarded-Port "";
proxy_set_header X-Forwarded-Prefix "";
proxy_set_header Forwarded "";
proxy_set_header X-Original-URL "";
