
#include <windows.h>
#include <stdio.h>
int main() {
    printf("Size: %zu\n", sizeof(GESTUREINFO));
    printf("hwndTarget: %zu\n", offsetof(GESTUREINFO, hwndTarget));
    printf("ptsLocation: %zu\n", offsetof(GESTUREINFO, ptsLocation));
    printf("dwInstanceID: %zu\n", offsetof(GESTUREINFO, dwInstanceID));
    printf("dwSequenceID: %zu\n", offsetof(GESTUREINFO, dwSequenceID));
    printf("ullArguments: %zu\n", offsetof(GESTUREINFO, ullArguments));
    return 0;
}

