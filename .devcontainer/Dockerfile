FROM mcr.microsoft.com/dotnet/sdk:8.0.100 AS build-env

RUN apt-get update && apt-get -y install --no-install-recommends \
   gpg \
   gpg-agent \
   jq \
   dirmngr

# Running gpg without first running mkdir works in a terminal inside the
# container. But when run from a dockerfile, gpg fails:
#
# gpg: keybox '/usr/share/keyrings/k6-archive-keyring.gpg' created
# gpg: failed to create temporary file '/root/.gnupg/.#lk0x000055aa87673290.f0f7333798af.8': No such file or directory
# gpg: connecting dirmngr at '/root/.gnupg/S.dirmngr' failed: No such file or directory
# gpg: keyserver receive failed: No dirmngr
#
# The errors are misleading as dirmngr is already installed. It's the
# /root/.gnupg directory that doesn't exist at this point.
RUN mkdir /root/.gnupg
RUN gpg --no-default-keyring --keyring /usr/share/keyrings/k6-archive-keyring.gpg --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D69
RUN echo "deb [signed-by=/usr/share/keyrings/k6-archive-keyring.gpg] https://dl.k6.io/deb stable main" | tee /etc/apt/sources.list.d/k6.list
RUN apt-get update
RUN apt-get install k6

# Prevent error from dotnet command when running the web application: "unable to
# configure HTTPS endpoint. No server certificate was specified, and the default
# developer certificate could not be found or is out of date".
RUN dotnet dev-certs https --trust