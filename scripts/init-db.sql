-- MicroservicesShop PostgreSQL Initialization Script
-- Enforces Database-Per-Service by provisioning independent databases and dedicated service roles.

-- 1. Create separate databases
CREATE DATABASE catalogdb;
CREATE DATABASE orderingdb;
CREATE DATABASE notificationsdb;

-- 2. Create dedicated service login roles
CREATE USER catalog_user WITH ENCRYPTED PASSWORD 'catalog_dev_password';
CREATE USER ordering_user WITH ENCRYPTED PASSWORD 'ordering_dev_password';
CREATE USER notifications_user WITH ENCRYPTED PASSWORD 'notifications_dev_password';

-- 3. Grant database ownership/privileges to dedicated service roles
GRANT ALL PRIVILEGES ON DATABASE catalogdb TO catalog_user;
GRANT ALL PRIVILEGES ON DATABASE orderingdb TO ordering_user;
GRANT ALL PRIVILEGES ON DATABASE notificationsdb TO notifications_user;

\connect catalogdb
GRANT ALL ON SCHEMA public TO catalog_user;

\connect orderingdb
GRANT ALL ON SCHEMA public TO ordering_user;

\connect notificationsdb
GRANT ALL ON SCHEMA public TO notifications_user;
