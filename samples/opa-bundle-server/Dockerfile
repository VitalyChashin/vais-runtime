# OPA bundle server — serves a pre-built bundle.tar.gz via nginx.
# Build after running sign-bundle.sh so the /bundles directory contains
# the signed (or unsigned) bundle.tar.gz.
#
# Build:
#   ./sign-bundle.sh          # or: opa build bundle/ -o bundle.tar.gz
#   docker build -t opa-bundle-server .
#
# Run:
#   docker run -p 8888:8888 opa-bundle-server

FROM nginx:1.27-alpine

COPY nginx.conf /etc/nginx/nginx.conf

# The signed bundle produced by sign-bundle.sh lands in this directory.
COPY bundle.tar.gz /bundles/bundle.tar.gz

EXPOSE 8888
