-- Crea los roles y la base NUEVA de DiagNET en un PostgreSQL local. No contiene contraseñas:
-- las recibe como variables de psql (:pwd_app y :pwd_pruebas) desde crear_base_local.ps1.
-- Es idempotente: no toca ninguna otra base ni rol (incluidas bases de implementaciones anteriores).
\set ON_ERROR_STOP on

-- Rol de la aplicación (dueño de diagnet_nueva). Sin SUPERUSER, sin CREATEDB.
SELECT format('CREATE ROLE diagnet_app LOGIN PASSWORD %L', :'pwd_app')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'diagnet_app') \gexec
SELECT format('ALTER ROLE diagnet_app LOGIN PASSWORD %L', :'pwd_app') \gexec

-- Rol de las pruebas: solo puede crear (y borrar) sus propias bases temporales.
SELECT format('CREATE ROLE diagnet_pruebas LOGIN CREATEDB PASSWORD %L', :'pwd_pruebas')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'diagnet_pruebas') \gexec
SELECT format('ALTER ROLE diagnet_pruebas LOGIN CREATEDB PASSWORD %L', :'pwd_pruebas') \gexec

-- Base nueva de desarrollo y demostración.
SELECT 'CREATE DATABASE diagnet_nueva OWNER diagnet_app'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'diagnet_nueva') \gexec

\echo
\echo 'Roles y base listos:'
SELECT rolname AS rol, rolcreatedb AS puede_crear_bases FROM pg_roles WHERE rolname IN ('diagnet_app', 'diagnet_pruebas') ORDER BY 1;
SELECT datname AS base, pg_get_userbyid(datdba) AS duenio FROM pg_database WHERE datname = 'diagnet_nueva';
