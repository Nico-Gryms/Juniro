# Juniro — application mobile (.NET MAUI)

Consultation des prédictions, des résultats et des stratégies, saisie des paris joués et courbe du ROI.
Android et Windows. Les données viennent de Supabase (projet « juniro »), alimenté chaque matin par
le service Windows (job `supabase-sync`).

Explication complète, mise en service et organisation du code : `DOCUMENTATION.md` à la racine du
projet, **chapitre 13**.

Démarrage rapide :

1. Visual Studio 2026 avec la charge de travail « Développement .NET MAUI ».
2. Ouvrir `App\Juniro.App.csproj`.
3. Cible « Windows Machine » (ou le téléphone Android branché en USB), puis F5.
4. Se connecter avec un compte créé dans Supabase (Authentication > Users).
   dotnet publish App\Juniro.App.csproj -f net10.0-android -c Release