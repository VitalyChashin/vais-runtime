#!/usr/bin/env bash
# sign-bundle.sh — build + optionally sign an OPA bundle for the Vais.Agents
# bundle server sample.
#
# Requirements:
#   - opa CLI >= 1.0  (https://www.openpolicyagent.org/docs/latest/cli/)
#   - openssl         (for RS256 key generation)
#
# Usage:
#   ./sign-bundle.sh [--sign]
#
#   --sign   Generate an RS256 key pair, sign the bundle, and write the
#            public key to bundle-signing-pub.pem.  Without this flag the
#            script builds an unsigned bundle (suitable for testing without
#            signature verification).
#
# Outputs:
#   bundle.tar.gz          — the OPA bundle archive (signed or unsigned)
#   bundle-signing-pub.pem — PEM public key (only when --sign is passed)
#   bundle-signing-key.pem — PEM private key (only when --sign is passed)
#                            *** keep this secret, never commit it ***
set -euo pipefail

SIGN=false
if [[ "${1:-}" == "--sign" ]]; then
    SIGN=true
fi

KEY_ID="vais-bundle-key"
PRIV_KEY="bundle-signing-key.pem"
PUB_KEY="bundle-signing-pub.pem"

echo "==> Building OPA bundle from bundle/ ..."
opa build bundle/ --output bundle.tar.gz
echo "    bundle.tar.gz written."

if [[ "$SIGN" == "true" ]]; then
    echo ""
    echo "==> Generating RS256 key pair ..."
    openssl genrsa -out "$PRIV_KEY" 2048 2>/dev/null
    openssl rsa -in "$PRIV_KEY" -pubout -out "$PUB_KEY" 2>/dev/null
    echo "    Private key: $PRIV_KEY  (keep secret — do not commit)"
    echo "    Public key:  $PUB_KEY"

    echo ""
    echo "==> Signing the bundle ..."
    opa sign bundle.tar.gz \
        --signing-key "$PRIV_KEY" \
        --signing-key-id "$KEY_ID" \
        --bundle \
        --output bundle.tar.gz
    echo "    Signed bundle.tar.gz written (key-id: $KEY_ID)."

    echo ""
    echo "==> Next steps:"
    echo "    1. Create a Kubernetes Secret with the public key:"
    echo "         kubectl create secret generic opa-bundle-signing-key \\"
    echo "           --from-file=key.pem=$PUB_KEY \\"
    echo "           --namespace vais-agents"
    echo ""
    echo "    2. Deploy the Helm chart with bundle + signing enabled:"
    echo "         helm upgrade --install vais-runtime deploy/helm/vais-agents-runtime \\"
    echo "           --set opa.enabled=true \\"
    echo "           --set opa.bundle.enabled=true \\"
    echo "           --set opa.bundle.url=http://opa-bundles.vais-agents.svc:8888 \\"
    echo "           --set opa.bundle.signing.enabled=true \\"
    echo "           --set opa.bundle.signing.existingSecret=opa-bundle-signing-key \\"
    echo "           --namespace vais-agents"
else
    echo ""
    echo "==> Unsigned bundle built.  Pass --sign to generate a key pair and"
    echo "    sign the bundle for production deployments."
fi
