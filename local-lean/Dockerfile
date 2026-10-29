# Adds the locally built ThetaData connector to a LEAN engine or research image.
# Build context: the connector's Release output folder (QuantConnect.ThetaData/bin/Release).
# BASE_IMAGE is the image to extend, for example lean-cli/engine:local or quantconnect/lean:latest.
ARG BASE_IMAGE
FROM ${BASE_IMAGE}
COPY QuantConnect.Lean.DataSource.ThetaData.dll QuantConnect.Lean.DataSource.ThetaData.pdb /Lean/Launcher/bin/Debug/
