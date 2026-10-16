#!/usr/bin/env bash
# Requête SQL en lecture sur la base du cluster de test : psql.sh "SELECT ..."
docker exec -i "${COMPOSE_PROJECT_NAME:-encoding-cluster-test}-postgres-1" psql -U encoding -d encoding -P pager=off -c "$1"
