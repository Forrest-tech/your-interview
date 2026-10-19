#!/bin/bash
# your-interview sandbox auto-start script
# Restores Postgres, RabbitMQ, backends, and frontend after runtime restart

set -e
export PATH=$HOME/workspace/.dotnet:$PATH
export TMPDIR=$HOME/workspace/tmp
# Bundled libpq for the pg16 binaries (survives VM replacement; apt-installed libpq5 does not)
export LD_LIBRARY_PATH=$HOME/workspace/your-interview/lib/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}

# 1. Postgres
if ! pg_isready -h localhost -p 5432 >/dev/null 2>&1; then
  echo "[$(date)] Starting Postgres..."
  # Ensure pguser exists and files are in place
  if ! id pguser >/dev/null 2>&1; then
    useradd -m pguser
  fi
  if [ ! -d /home/pguser/pg16 ]; then
    cp -r ~/workspace/tmp/pg16 /home/pguser/
    chown -R pguser:pguser /home/pguser/pg16
  fi
  if [ ! -d /home/pguser/pgdata ]; then
    su pguser -c "/home/pguser/pg16/usr/lib/postgresql/16/bin/initdb -D /home/pguser/pgdata -U postgres"
    # Set password and create DB after start
    NEED_SETUP=1
  fi
  mkdir -p /var/run/postgresql
  chown pguser:pguser /var/run/postgresql
  chown -R pguser:pguser /home/pguser/pgdata 2>/dev/null || true
  su pguser -c "/home/pguser/pg16/usr/lib/postgresql/16/bin/pg_ctl -D /home/pguser/pgdata -l /home/pguser/pg.log start"
  sleep 5
  if [ "$NEED_SETUP" = "1" ]; then
    PGPASSWORD=postgres psql -h localhost -U postgres -c "ALTER USER postgres PASSWORD 'postgres123';" 2>/dev/null || true
    PGPASSWORD=postgres123 psql -h localhost -U postgres -c "CREATE DATABASE yourinterview;" 2>/dev/null || true
  fi
fi

# 1b. RabbitMQ (Jobs backend publishes via MassTransit/RabbitMQ; crashes without it)
if ! command -v rabbitmq-server >/dev/null 2>&1; then
  echo "[$(date)] Installing RabbitMQ (apt package lost after VM replacement)..."
  sudo -n DEBIAN_FRONTEND=noninteractive apt-get update >/dev/null 2>&1 || true
  sudo -n DEBIAN_FRONTEND=noninteractive apt-get install -y rabbitmq-server >/tmp/rabbitmq-install.log 2>&1
fi
if ! (echo > /dev/tcp/localhost/5672) 2>/dev/null; then
  echo "[$(date)] Starting RabbitMQ..."
  sudo -n rabbitmq-server -detached >/dev/null 2>&1
  sleep 10
fi

# 2. Identity backend (5262)
if ! curl -s -o /dev/null -w "%{http_code}" http://localhost:5262/api/auth/login --max-time 3 2>/dev/null | grep -q "405\|400"; then
  echo "[$(date)] Starting Identity..."
  export ConnectionStrings__Identity="Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres123"
  export Seed__AdminPassword="Aa200808"
  export ASPNETCORE_URLS="http://localhost:5262"
  cd ~/workspace/your-interview
  nohup dotnet run --project src/Services.Identity --no-build > /tmp/identity.log 2>&1 &
fi

# 3. Jobs backend (5125)
if ! curl -s http://localhost:5125/api/jobs/stats --max-time 3 2>/dev/null | grep -q "total"; then
  # Try with auth check instead - just check if port is open
  if ! (echo > /dev/tcp/localhost/5125) 2>/dev/null; then
    echo "[$(date)] Starting Jobs..."
    export ConnectionStrings__Jobs="Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres123"
    export ASPNETCORE_URLS="http://localhost:5125"
    # 2026-10-19:Playwright 出站经代理(沙盒)
    export HTTPS_PROXY="${HTTPS_PROXY:-http://hatch-egress-proxy:3128}"
    export HTTP_PROXY="${HTTP_PROXY:-http://hatch-egress-proxy:3128}"
    cd ~/workspace/your-interview
    nohup dotnet run --project src/Services.Jobs --no-build > /tmp/jobs.log 2>&1 &
  fi
fi

# 4. Frontend (4200)
if ! curl -s -o /dev/null http://localhost:4200/ --max-time 3 2>/dev/null; then
  echo "[$(date)] Starting frontend..."
  cd ~/workspace/your-interview/web
  if [ ! -f /tmp/proxy.local.json ]; then
    cat > /tmp/proxy.local.json << 'EOF'
{
  "/api/auth": { "target": "http://127.0.0.1:5262", "secure": false, "changeOrigin": true },
  "/api": { "target": "http://127.0.0.1:5125", "secure": false, "changeOrigin": true }
}
EOF
  fi
  nohup npx ng serve --proxy-config /tmp/proxy.local.json --port 4200 --host 0.0.0.0 > /tmp/ng.log 2>&1 &
fi

echo "[$(date)] Sandbox check complete"
