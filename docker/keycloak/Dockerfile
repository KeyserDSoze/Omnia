FROM quay.io/keycloak/keycloak:26.4
# The Tao realm (client, hardcoded tenant claim, API audience) is imported on the first start only.
COPY tao-realm.json /opt/keycloak/data/import/tao-realm.json
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--import-realm", "--http-enabled=true", "--http-relative-path=/auth", "--proxy-headers=xforwarded", "--health-enabled=true"]
