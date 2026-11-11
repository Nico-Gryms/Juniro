/* =============================================================================
   Juniro_Evo - Compte SQL de l'application Windows de stratégies
   -----------------------------------------------------------------------------
   Crée ce dont l'application a besoin pour se connecter, sans passer par le compte
   du scraper :

     lab          schéma des futures tables de l'application (stratégies créées dans
                  l'application, demandes de calcul, stratégies actives en paris fictifs)
     lab_app      rôle qui porte les droits :
                    lecture seule  sur core, ml, sim, app
                    lecture et écriture sur lab (et exécution de ses procédures)
                    aucun droit sur dbo (données brutes) ni sur feat
     juniro_lab   connexion SQL Server + utilisateur de la base, membre de lab_app

   L'application ne modifie donc jamais les matchs, les cotes ni les résultats de
   simulation : elle lit, et n'écrit que dans son propre schéma.

   Étapes :
     1. Vérifie que le serveur accepte les connexions par mot de passe.
     2. Crée la connexion juniro_lab (seulement si elle n'existe pas).
     3. Crée le schéma lab, le rôle lab_app, l'utilisateur, et (ré)applique les droits.
     4. Affiche les droits obtenus, pour contrôle.

   Utilisation (dans SSMS, avec un compte administrateur du serveur) :
     - saisir un mot de passe dans @MotDePasse ci-dessous (12 caractères au moins) ;
     - exécuter ;
     - NE PAS enregistrer le fichier avec le mot de passe (il est envoyé sur GitHub).
   Lancé tel quel (mot de passe vide) : rien n'est créé si la connexion n'existe pas.

   Script rejouable : si la connexion existe déjà, son mot de passe n'est pas touché et
   seuls les droits sont réappliqués. Pour changer le mot de passe :
     ALTER LOGIN [juniro_lab] WITH PASSWORD = N'nouveau mot de passe';

   Où modifier : la liste des droits est dans le bloc 3 (un GRANT par schéma).
   Documentation : DOCUMENTATION.md (racine du projet), chapitre 14.
   ============================================================================= */
USE [Juniro_Evo];
GO

/* -----------------------------------------------------------------------------
   1 et 2. Connexion SQL Server juniro_lab
   ----------------------------------------------------------------------------- */
DECLARE @MotDePasse NVARCHAR(128) = N'';          -- <<< à saisir avant d'exécuter, à ne pas enregistrer

IF CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS INT) = 1
BEGIN
    RAISERROR(N'Le serveur n''accepte que l''authentification Windows : activez le mode mixte (Propriétés du serveur > Sécurité), redémarrez SQL Server, puis relancez ce script.', 16, 1);
    SET NOEXEC ON;                                -- le reste du script est ignoré
END

IF SUSER_ID(N'juniro_lab') IS NOT NULL
    PRINT N'Connexion juniro_lab déjà présente : mot de passe inchangé, droits réappliqués.';
ELSE IF LEN(@MotDePasse) < 12
BEGIN
    RAISERROR(N'Rien n''a été créé : saisissez dans @MotDePasse un mot de passe d''au moins 12 caractères, puis relancez.', 16, 1);
    SET NOEXEC ON;                                -- le reste du script est ignoré
END
ELSE
BEGIN
    -- CREATE LOGIN n'accepte pas de variable : la commande est construite, le mot de passe
    -- étant protégé par QUOTENAME (apostrophes doublées).
    DECLARE @sql NVARCHAR(MAX) =
        N'CREATE LOGIN [juniro_lab] WITH PASSWORD = ' + QUOTENAME(@MotDePasse, N'''')
      + N', DEFAULT_DATABASE = [Juniro_Evo], CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;';
    EXEC sys.sp_executesql @sql;
    PRINT N'Connexion juniro_lab créée.';
END
GO

/* -----------------------------------------------------------------------------
   3. Schéma, rôle, utilisateur et droits
   ----------------------------------------------------------------------------- */
IF SCHEMA_ID(N'lab') IS NULL EXEC(N'CREATE SCHEMA lab');
GO

IF DATABASE_PRINCIPAL_ID(N'lab_app') IS NULL
    CREATE ROLE [lab_app];
GO

IF DATABASE_PRINCIPAL_ID(N'juniro_lab') IS NULL
    CREATE USER [juniro_lab] FOR LOGIN [juniro_lab] WITH DEFAULT_SCHEMA = [lab];
GO

IF ISNULL(IS_ROLEMEMBER(N'lab_app', N'juniro_lab'), 0) = 0
    ALTER ROLE [lab_app] ADD MEMBER [juniro_lab];
GO

-- Lecture seule : vues sources, modèles et prédictions, simulations, vues de consultation.
-- Les vues de core et de app lisent dbo sans que le rôle ait de droit direct sur dbo.
GRANT SELECT ON SCHEMA::[core] TO [lab_app];
GRANT SELECT ON SCHEMA::[ml]   TO [lab_app];
GRANT SELECT ON SCHEMA::[sim]  TO [lab_app];
GRANT SELECT ON SCHEMA::[app]  TO [lab_app];

-- Lecture et écriture : uniquement le schéma de l'application.
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[lab] TO [lab_app];
GO

/* -----------------------------------------------------------------------------
   4. Contrôle : la connexion, puis les droits du rôle
      (attendu : SELECT sur app, core, ml, sim ; 5 droits sur lab, soit 9 lignes)
   ----------------------------------------------------------------------------- */
SELECT sp.name AS Connexion, sp.is_disabled AS Desactivee, sp.default_database_name AS BaseParDefaut,
       sp.create_date AS CreeeLe, sl.is_policy_checked AS PolitiqueMotDePasse, sl.is_expiration_checked AS Expiration
FROM sys.server_principals sp
LEFT JOIN sys.sql_logins sl ON sl.principal_id = sp.principal_id
WHERE sp.name = N'juniro_lab';

SELECT pr.name AS Role, SCHEMA_NAME(pe.major_id) AS SchemaName, pe.permission_name AS Droit
FROM sys.database_permissions pe
JOIN sys.database_principals pr ON pr.principal_id = pe.grantee_principal_id
WHERE pr.name = N'lab_app' AND pe.class_desc = 'SCHEMA' AND pe.state_desc = 'GRANT'
ORDER BY SchemaName, Droit;
GO

SET NOEXEC OFF;
GO

/* Essai depuis un autre poste (invite de commandes, le mot de passe est demandé) :
     sqlcmd -S 100.80.8.107,49999 -d Juniro_Evo -U juniro_lab -Q "SELECT COUNT(*) AS Lignes FROM sim.Runs"
   Doit échouer (droit refusé), ce qui prouve que le compte ne touche pas aux données brutes :
     sqlcmd -S 100.80.8.107,49999 -d Juniro_Evo -U juniro_lab -Q "SELECT TOP 1 * FROM dbo.Matches"
*/
