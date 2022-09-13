#!/bin/sh
# wait-for-postgres.sh

until PGPASSWORD=archiver psql -h db -p 5432 -U "archiver" lobi -c '\q'; do
  >&2 echo "Postgres is unavailable - sleeping"
  sleep 10
done

#>&2 echo "Postgres is up - executing command"
#exec "$@"
