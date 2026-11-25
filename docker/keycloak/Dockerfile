FROM quay.io/keycloak/keycloak:26.4

# Build options are baked into the image so the container starts with --optimized
# (no re-augmentation on every start: faster and lighter on the shared plan).
ENV KC_DB=postgres KC_HEALTH_ENABLED=true KC_HTTP_RELATIVE_PATH=/auth
RUN /opt/keycloak/bin/kc.sh build

# The Tao realm (client, hardcoded tenant claim, API audience) is imported on the first start only.
COPY tao-realm.json /opt/keycloak/data/import/tao-realm.json
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--optimized", "--import-realm", "--http-enabled=true", "--proxy-headers=xforwarded"]
