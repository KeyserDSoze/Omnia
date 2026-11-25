proxy_http_version 1.1;
proxy_set_header Connection "";
proxy_set_header Host $host;
# Overwrite (never append) forwarding headers so clients cannot inject them.
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto $tao_forwarded_proto;
proxy_set_header X-Forwarded-Host $host;
proxy_set_header X-Forwarded-Port "";
proxy_set_header X-Forwarded-Prefix "";
proxy_set_header Forwarded "";
proxy_set_header X-Original-URL "";
