#!/bin/sh
# DataLogXE - A simple and high performance data logger.
# Copyright (C) 2026 datalogxe@gmail.com
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program.  If not, see <http://www.gnu.org/licenses/>.

# Starts as root only to hand the volume to the user "app" (volumes of older versions belong to root),
# then runs DataLogApi as "app".
set -e

if [ "$(id -u)" = "0" ]; then
  mkdir -p /app/data /app/logs
  # Up to 0.0.200 the OPC UA certificates had a volume of their own at /app/opcua. When it is still mounted,
  # its certificates move into the data volume once, so servers that trust the client certificate keep it.
  if [ -d /app/opcua/own ] && [ ! -e /app/data/opcua ]; then
    cp -a /app/opcua /app/data/opcua
    echo "Took over the OPC UA certificates from /app/opcua"
  fi
  chown -R app:app /app/data /app/logs
  exec setpriv --reuid=app --regid=app --init-groups "$@"
fi

exec "$@"
