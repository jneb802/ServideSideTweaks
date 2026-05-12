#!/bin/bash

# Valheim Mod Template Renaming Script
# Usage: ./rename-mod.sh <NewModName> [Author]

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Check arguments
if [ $# -lt 1 ]; then
    echo -e "${RED}❌ Usage: ./rename-mod.sh <NewModName> [Author]${NC}"
    echo "Example: ./rename-mod.sh MyAwesomeMod"
    echo "Example: ./rename-mod.sh MyAwesomeMod MyUsername"
    exit 1
fi

NEW_MOD_NAME="$1"
NEW_AUTHOR="${2:-warpalicious}"  # Default to current author if not specified

# Validate mod name (no spaces, special chars)
if [[ ! "$NEW_MOD_NAME" =~ ^[a-zA-Z][a-zA-Z0-9]*$ ]]; then
    echo -e "${RED}❌ Invalid mod name. Use only letters and numbers, starting with a letter.${NC}"
    exit 1
fi

# Current values to replace
OLD_MOD_NAME="Template"
OLD_AUTHOR="warpalicious"
OLD_NAMESPACE="Template"
OLD_GUID="warpalicious.Template"
OLD_ASSEMBLY="Template"

# New values
NEW_NAMESPACE="$NEW_MOD_NAME"
NEW_GUID="$NEW_AUTHOR.$NEW_MOD_NAME"
NEW_ASSEMBLY="$NEW_MOD_NAME"

echo -e "${BLUE}🔄 Renaming Valheim mod template...${NC}"
echo -e "${YELLOW}Old name: $OLD_MOD_NAME${NC}"
echo -e "${GREEN}New name: $NEW_MOD_NAME${NC}"
echo -e "${YELLOW}Old author: $OLD_AUTHOR${NC}"
echo -e "${GREEN}New author: $NEW_AUTHOR${NC}"
echo ""

# Create backup
BACKUP_DIR="../${NEW_MOD_NAME}_backup_$(date +%Y%m%d_%H%M%S)"
echo -e "${BLUE}📦 Creating backup at: $BACKUP_DIR${NC}"
cp -r . "$BACKUP_DIR"

# Function to replace in file
replace_in_file() {
    local file="$1"
    local old_text="$2"
    local new_text="$3"
    
    if [[ "$OSTYPE" == "darwin"* ]]; then
        # macOS
        sed -i '' "s|$old_text|$new_text|g" "$file"
    else
        # Linux
        sed -i "s|$old_text|$new_text|g" "$file"
    fi
}

# Update C# source files
echo -e "${BLUE}🔧 Updating source files...${NC}"

# Update namespaces, class names, and strings in all .cs files
find . -name "*.cs" -type f | while read -r file; do
    echo "  📝 Updating: $file"
    
    # Replace namespace
    replace_in_file "$file" "namespace Template" "namespace $NEW_NAMESPACE"
    
    # Replace class names
    replace_in_file "$file" "TemplatePlugin" "${NEW_MOD_NAME}Plugin"
    replace_in_file "$file" "class Template" "class $NEW_MOD_NAME"
    
    # Replace string constants
    replace_in_file "$file" "ModName = \"Template\"" "ModName = \"$NEW_MOD_NAME\""
    replace_in_file "$file" "Author = \"$OLD_AUTHOR\"" "Author = \"$NEW_AUTHOR\""
    replace_in_file "$file" "$OLD_GUID" "$NEW_GUID"
    
    # Replace log source names
    replace_in_file "$file" "CreateLogSource(ModName)" "CreateLogSource(ModName)"
    replace_in_file "$file" "Template " "$NEW_MOD_NAME "
done

# Update project files
echo -e "${BLUE}🔧 Updating project files...${NC}"

# Update .csproj file
if [ -f "Template.csproj" ]; then
    echo "  📝 Updating: Template.csproj"
    replace_in_file "Template.csproj" "$OLD_NAMESPACE" "$NEW_NAMESPACE"
    replace_in_file "Template.csproj" "$OLD_ASSEMBLY" "$NEW_ASSEMBLY"
    replace_in_file "Template.csproj" "Template" "$NEW_ASSEMBLY"
fi

# Update AssemblyInfo.cs
if [ -f "Properties/AssemblyInfo.cs" ]; then
    echo "  📝 Updating: Properties/AssemblyInfo.cs"
    replace_in_file "Properties/AssemblyInfo.cs" "$OLD_ASSEMBLY" "$NEW_ASSEMBLY"
    replace_in_file "Properties/AssemblyInfo.cs" "Template" "$NEW_MOD_NAME"
fi

# Update build scripts
echo -e "${BLUE}🔧 Updating build scripts...${NC}"

# Update build.sh
if [ -f "build.sh" ]; then
    echo "  📝 Updating: build.sh"
    replace_in_file "build.sh" "MOD_NAME=\"Template\"" "MOD_NAME=\"$NEW_ASSEMBLY\""
    replace_in_file "build.sh" "Template" "$NEW_ASSEMBLY"
fi

# Update monitor-logs.sh
if [ -f "monitor-logs.sh" ]; then
    echo "  📝 Updating: monitor-logs.sh"
    replace_in_file "monitor-logs.sh" "MOD_NAME=\"Template\"" "MOD_NAME=\"$NEW_ASSEMBLY\""
    replace_in_file "monitor-logs.sh" "MOD_GUID=\"warpalicious.Template\"" "MOD_GUID=\"$NEW_GUID\""
    replace_in_file "monitor-logs.sh" "Template" "$NEW_MOD_NAME"
fi

# Rename files
echo -e "${BLUE}📁 Renaming files...${NC}"

# Rename Template.cs to NewModName.cs
if [ -f "Source/Template.cs" ]; then
    echo "  📁 Renaming: Source/Template.cs -> Source/$NEW_MOD_NAME.cs"
    mv "Source/Template.cs" "Source/$NEW_MOD_NAME.cs"
fi

# Rename project file
if [ -f "Template.csproj" ]; then
    echo "  📁 Renaming: Template.csproj -> $NEW_MOD_NAME.csproj"
    mv "Template.csproj" "$NEW_MOD_NAME.csproj"
fi

# Update solution file if it exists
if [ -f "Template.sln" ]; then
    echo "  📝 Updating: Template.sln"
    replace_in_file "Template.sln" "Template" "$NEW_MOD_NAME"
    mv "Template.sln" "$NEW_MOD_NAME.sln"
fi

# Generate new GUID for project
NEW_PROJECT_GUID=$(uuidgen)
if [ -f "$NEW_MOD_NAME.csproj" ]; then
    echo "  🔑 Generating new project GUID: $NEW_PROJECT_GUID"
    replace_in_file "$NEW_MOD_NAME.csproj" "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}" "{$NEW_PROJECT_GUID}"
fi

if [ -f "Properties/AssemblyInfo.cs" ]; then
    replace_in_file "Properties/AssemblyInfo.cs" "A1B2C3D4-E5F6-7890-ABCD-EF1234567890" "$NEW_PROJECT_GUID"
fi

# Test build
echo -e "${BLUE}🔨 Testing build...${NC}"
if command -v dotnet >/dev/null 2>&1; then
    if dotnet build "$NEW_MOD_NAME.csproj"; then
        echo -e "${GREEN}✅ Build successful!${NC}"
    else
        echo -e "${RED}❌ Build failed. Please check for errors.${NC}"
    fi
else
    echo -e "${YELLOW}⚠️  dotnet not found. Skipping build test.${NC}"
fi

echo ""
echo -e "${GREEN}🎉 Mod renaming complete!${NC}"
echo -e "${YELLOW}📋 Summary:${NC}"
echo "  • Mod name: $OLD_MOD_NAME → $NEW_MOD_NAME"
echo "  • Author: $OLD_AUTHOR → $NEW_AUTHOR"
echo "  • Namespace: $OLD_NAMESPACE → $NEW_NAMESPACE"
echo "  • GUID: $OLD_GUID → $NEW_GUID"
echo "  • Assembly: $OLD_ASSEMBLY → $NEW_ASSEMBLY"
echo ""
echo -e "${BLUE}📁 Backup created at: $BACKUP_DIR${NC}"
echo -e "${GREEN}🚀 Your mod is ready for development!${NC}"
echo ""
echo -e "${YELLOW}💡 Next steps:${NC}"
echo "  1. Update README.md with your mod description"
echo "  2. Start coding your mod features in Source/$NEW_MOD_NAME.cs"
echo "  3. Build and test: ./build.sh" 