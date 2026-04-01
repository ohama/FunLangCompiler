# Roadmap: v16.0 FunLang AST 동기화

## Phase 58: Namespace 제거

**Goal:** FunLang이 AST에서 삭제한 NamespaceDecl/NamespacedModule 참조를 Compiler에서 제거하여 빌드 복구

**Requirements:** NS-01 ~ NS-05, TEST-01

**Success Criteria:**
1. `dotnet build` 성공 (0 errors)
2. `grep -n "NamespaceDecl\|NamespacedModule" src/` 결과가 0건
3. 기존 232 E2E 테스트 전부 통과

**Approach:**
- Elaboration.fs의 NamespaceDecl 패턴 매치 4곳 제거
- NamespacedModule 패턴 1곳 제거
- 관련 주석 정리

---

## Phase 59: 중첩 모듈 Qualified Access

**Goal:** Outer.Inner.value 형태의 중첩 모듈 qualified access 지원

**Requirements:** NEST-01 ~ NEST-04, TEST-02

**Success Criteria:**
1. `Outer.Inner.value` qualified access가 정상 동작
2. `open Outer.Inner` 후 unqualified access 정상 동작
3. 기존 테스트 + 새 중첩 모듈 테스트 통과
4. 기존 단일 모듈 동작(List.map, Option.map 등)에 regression 없음

**Approach:**
- flattenDecls: 중첩 ModuleDecl에서 modName + "_" + name으로 full prefix 전달
- collectModuleMembers: 중첩 모듈을 "Outer.Inner" 복합 키로 등록
- FieldAccess: 중첩 FieldAccess(FieldAccess(Constructor("Outer"), "Inner"), "foo") 패턴 처리
- OpenDecl: multi-segment path를 "." join하여 복합 키로 lookup

---
*Created: 2026-04-01*
*Milestone: v16.0 FunLang AST 동기화*
*Phase numbering continues from v15.0 (Phase 57)*
