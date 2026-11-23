#!/bin/bash

VERSION=$(grep -Po '(?<=SimpleVersion = ")[0-9\.]+' Source/Common/Version.cs)

git submodule update --init --recursive || { echo 'git submodule update FAILED' ; exit 1; }

cd Source || exit
dotnet build --configuration Release || { echo 'dotnet build FAILED' ; exit 1; }
dotnet build Client/Multiplayer.csproj --configuration Release -p:RimWorldVersion=1.7 || { echo 'RimWorld 1.7 build FAILED' ; exit 1; }
cd ..

rm -rf Multiplayer/
mkdir -p Multiplayer
cd Multiplayer || exit

cp -r ../About ../Textures ../LoadFolders.xml ../1.6 ../1.7 .

GIT_COMMIT=$(git rev-parse --short HEAD 2>&1)
GIT_COMMIT_STATUS=$?
if [ $GIT_COMMIT_STATUS -eq 0 ]; then
  FULL_VERSION="${VERSION} (${GIT_COMMIT})"
else
  FULL_VERSION="${VERSION}"
  echo "WARN: Failed to check git commit: ${GIT_COMMIT}"
fi
sed -i "/<supportedVersions>/ a \ \ \ \ <li>1.5</li>" About/About.xml
sed -i "s/<modVersion>.*<\/modVersion>.*\$/<modVersion>${FULL_VERSION}<\/modVersion>/" About/About.xml

for game_version in 1.6 1.7; do
  cp -r ../Defs ../Languages "$game_version/"
  rm -f "$game_version"/Languages/{.git,LICENSE,README.md}
done

# Past versions
git clone -b rw-1.5 --depth=1 --single-branch --recurse-submodules https://github.com/rwmt/Multiplayer.git 1.5 || { echo 'Git cloning 1.5 FAILED' ; exit 1; }
shopt -s extglob
shopt -s dotglob
rm -rf -- 1.5/!(Languages|Assemblies|AssembliesCustom|Defs)
rm -f 1.5/Languages/.git 1.5/Languages/LICENSE 1.5/Languages/README.md

cd ..

# Zip for Github releases
rm -f Multiplayer-v$VERSION.zip
zip -r -q Multiplayer-v$VERSION.zip Multiplayer

echo "Ok, $PWD/Multiplayer-v$VERSION.zip ready for uploading to Workshop"
